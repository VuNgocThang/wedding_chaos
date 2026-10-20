"""
Wedding Chaos - procedural audio generator.

Every sound effect and the background music are synthesised here (no third-party samples),
so the project owns all of its audio. Run:

    python gen_audio.py <out_dir>

Writes <out_dir>/*.wav, then (if ffmpeg is on PATH) converts them to .ogg next to them.
Copy the .ogg files to Assets/_Game/Resources/Audio/.
"""
import os
import subprocess
import sys

import numpy as np
from scipy import signal

SR = 44100
rng = np.random.default_rng(7)


# ---------------------------------------------------------------- helpers

def t_axis(dur):
    return np.arange(int(dur * SR)) / SR


def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12)


def env_ad(n, attack=0.005, decay=0.2, curve=4.0):
    """Fast attack, exponential decay over the whole buffer length n."""
    t = np.arange(n) / SR
    a = np.clip(t / max(attack, 1e-4), 0, 1)
    d = np.exp(-curve * np.maximum(t - attack, 0) / max(decay, 1e-4))
    return a * d


def fade(x, fin=0.002, fout=0.01):
    x = x.copy()
    i, o = int(fin * SR), int(fout * SR)
    if i > 0:
        x[:i] *= np.linspace(0, 1, i)
    if o > 0:
        x[-o:] *= np.linspace(1, 0, o)
    return x


def sweep(f0, f1, dur, shape="exp"):
    """Phase of a sine sweeping f0 -> f1."""
    n = int(dur * SR)
    if shape == "exp":
        f = f0 * (f1 / f0) ** np.linspace(0, 1, n)
    else:
        f = np.linspace(f0, f1, n)
    return 2 * np.pi * np.cumsum(f) / SR


def sine(f, dur):
    return np.sin(2 * np.pi * f * t_axis(dur))


def tri(f, dur):
    ph = (f * t_axis(dur)) % 1.0
    return 4 * np.abs(ph - 0.5) - 1


def soft_square(f, dur, harmonics=7):
    t = t_axis(dur)
    x = np.zeros_like(t)
    for k in range(1, harmonics * 2, 2):
        x += np.sin(2 * np.pi * f * k * t) / k
    return x * 0.8


def saw(f, dur, harmonics=12):
    t = t_axis(dur)
    x = np.zeros_like(t)
    for k in range(1, harmonics + 1):
        x += np.sin(2 * np.pi * f * k * t) / k
    return x * 0.6


def noise(dur):
    return rng.uniform(-1, 1, int(dur * SR))


def lowpass(x, fc, order=2):
    b, a = signal.butter(order, min(fc, SR / 2 - 100) / (SR / 2), "low")
    return signal.lfilter(b, a, x)


def highpass(x, fc, order=2):
    b, a = signal.butter(order, fc / (SR / 2), "high")
    return signal.lfilter(b, a, x)


def bandpass(x, lo, hi, order=2):
    b, a = signal.butter(order, [lo / (SR / 2), min(hi, SR / 2 - 100) / (SR / 2)], "band")
    return signal.lfilter(b, a, x)


def bell(f, dur, ratio=3.5, index=2.2, decay=None):
    """FM bell / celesta."""
    t = t_axis(dur)
    decay = decay or dur
    e = np.exp(-4.5 * t / decay)
    mod = index * np.exp(-6 * t / decay) * np.sin(2 * np.pi * f * ratio * t)
    x = np.sin(2 * np.pi * f * t + mod) * e
    x += 0.25 * np.sin(2 * np.pi * f * 2.0 * t) * np.exp(-9 * t / decay)
    return fade(x, 0.002, 0.02)


def pluck(f, dur, bright=0.5):
    """Karplus-Strong plucked string."""
    n = int(dur * SR)
    period = max(2, int(SR / f))
    buf = rng.uniform(-1, 1, period)
    buf = lowpass(buf, 1500 + bright * 6000, 1)
    out = np.zeros(n)
    damp = 0.996
    for i in range(n):
        out[i] = buf[i % period]
        buf[i % period] = damp * 0.5 * (buf[i % period] + buf[(i + 1) % period])
    return fade(out, 0.001, 0.02)


def pad(freqs, dur):
    t = t_axis(dur)
    x = np.zeros_like(t)
    for f in freqs:
        for det in (-0.004, 0.0, 0.005):
            x += saw(f * (1 + det), dur, 6)
    x = lowpass(x, 1400, 2)
    e = np.minimum(1, t / 0.25) * np.minimum(1, (dur - t) / 0.3)
    return x * e / (len(freqs) * 3)


def reverb(x, wet=0.25, length=1.2, damp=3000):
    """Convolution with a decaying-noise impulse response (cheap small-hall)."""
    n = int(length * SR)
    ir = rng.normal(0, 1, n) * np.exp(-6 * np.arange(n) / n)
    ir = lowpass(ir, damp, 1)
    ir /= np.sqrt(np.sum(ir ** 2))
    y = signal.fftconvolve(x, ir)[: len(x) + n]
    out = np.zeros(len(y))
    out[: len(x)] += x * (1 - wet)
    out += y * wet
    return out


def mix(*parts):
    """parts: (signal, start_seconds, gain)."""
    end = max(int(s * SR) + len(p) for p, s, g in parts)
    out = np.zeros(end)
    for p, s, g in parts:
        i = int(s * SR)
        out[i:i + len(p)] += p * g
    return out


def normalize(x, peak=0.89):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def loudness(x, target_db=-18.0, max_gain_db=8.0):
    """Even out perceived level between effects: peak-normalise, then push quiet ones up
    (bounded) through a soft limiter so short transients do not clip."""
    x = normalize(x, 0.85)
    rms_db = 20 * np.log10(np.sqrt(np.mean(x ** 2)) + 1e-9)
    gain = 10 ** (min(max_gain_db, max(0.0, target_db - rms_db)) / 20)
    y = np.tanh(x * gain * 1.1) / np.tanh(1.1)
    return normalize(y, 0.9)


def trim_tail(x, thresh=1e-3):
    idx = np.where(np.abs(x) > thresh)[0]
    return x[: idx[-1] + int(0.02 * SR)] if len(idx) else x


# ---------------------------------------------------------------- sound effects

def sfx_ui_tap():
    x = np.sin(sweep(900, 1250, 0.06)) * env_ad(int(0.06 * SR), 0.002, 0.05)
    return x * 0.6


def sfx_select():
    d = 0.11
    body = np.sin(sweep(380, 920, d)) * env_ad(int(d * SR), 0.003, 0.09)
    click = highpass(noise(0.008), 3000) * 0.25
    return mix((body, 0, 0.8), (click, 0, 1))


def sfx_lift():
    d = 0.18
    n = bandpass(noise(d), 600, 3500) * np.sin(np.linspace(0, np.pi, int(d * SR))) ** 2
    tone = np.sin(sweep(500, 1100, d)) * env_ad(int(d * SR), 0.01, 0.15) * 0.25
    return n * 0.5 + tone


def sfx_deny():
    a = lowpass(soft_square(233, 0.08), 1800) * env_ad(int(0.08 * SR), 0.003, 0.07)
    b = lowpass(soft_square(196, 0.12), 1800) * env_ad(int(0.12 * SR), 0.003, 0.1)
    return mix((a, 0, 0.45), (b, 0.085, 0.45))


def sfx_seat():
    d = 0.16
    plop = np.sin(sweep(420, 140, d)) * env_ad(int(d * SR), 0.002, 0.12)
    thump = lowpass(noise(0.05), 400) * env_ad(int(0.05 * SR), 0.001, 0.04)
    ding = bell(midi(84), 0.5, ratio=2.0, index=1.0) * 0.18
    return reverb(mix((plop, 0, 0.8), (thump, 0, 0.6), (ding, 0.06, 1)), 0.15, 0.6)


def sfx_bench():
    d = 0.22
    thud = np.sin(sweep(160, 70, d)) * env_ad(int(d * SR), 0.003, 0.16)
    cushion = lowpass(noise(d), 700) * env_ad(int(d * SR), 0.01, 0.14)
    return mix((thud, 0, 0.9), (cushion, 0, 0.5))


def sfx_table_done():
    notes = [72, 76, 79, 84]
    parts = [(bell(midi(n), 1.1, ratio=3.0, index=1.6), i * 0.075, 0.5) for i, n in enumerate(notes)]
    shimmer = [(bell(midi(96 + rng.integers(0, 8)), 0.35, 4.0, 1.0), 0.3 + i * 0.045, 0.12) for i in range(8)]
    return reverb(mix(*parts, *shimmer), 0.3, 1.4)


def sfx_cheer():
    """A little round of applause + a 'yay' chord."""
    parts = []
    for i in range(38):
        clap = bandpass(noise(0.03), 900, 5000) * env_ad(int(0.03 * SR), 0.001, 0.02)
        s = abs(rng.normal(0.35, 0.25))
        parts.append((clap, min(s, 1.1), 0.25 + rng.uniform(0, 0.25)))
    yay = pad([midi(60), midi(64), midi(67)], 0.7) * 0.5
    return reverb(mix(*parts, (yay, 0.0, 0.5)), 0.25, 1.0)


def sfx_drama_up():
    """Cartoon 'uh-oh'."""
    def note(f0, f1, d):
        return lowpass(saw(1, d) * 0 + np.sin(sweep(f0, f1, d)) + 0.4 * np.sin(2 * sweep(f0, f1, d)), 2500) * env_ad(int(d * SR), 0.01, d * 0.9, 2.5)
    a = note(392, 370, 0.16)
    b = note(311, 262, 0.3)
    vib = 1 + 0.25 * np.sin(2 * np.pi * 9 * t_axis(0.3))
    return reverb(mix((a, 0, 0.5), (b * vib, 0.17, 0.5)), 0.15, 0.6)


def sfx_drama_big():
    """Dramatic 'dun-dun' + crowd gasp."""
    def stab(fs, d):
        x = sum(lowpass(saw(f, d, 10), 1600) for f in fs)
        return x * env_ad(int(d * SR), 0.005, d * 0.8, 3)
    a = stab([midi(43), midi(55), midi(58)], 0.25)
    b = stab([midi(42), midi(54), midi(57)], 0.7)
    return reverb(mix((a, 0, 0.28), (b, 0.3, 0.3), (sfx_gasp() * 0.6, 0.2, 1)), 0.25, 1.2)


def sfx_gasp():
    """Crowd 'ooooh' - several formant-filtered voices."""
    d = 1.0
    t = t_axis(d)
    out = np.zeros(len(t))
    for v in range(7):
        f0 = rng.uniform(170, 290)
        glide = 1 + 0.18 * np.sin(np.pi * np.clip(t / d, 0, 1)) - 0.08 * t
        ph = 2 * np.pi * np.cumsum(f0 * glide * (1 + 0.01 * np.sin(2 * np.pi * rng.uniform(4, 6) * t))) / SR
        src = sum(np.sin(k * ph) / k for k in range(1, 18))
        voice = bandpass(src, 380, 620) * 1.0 + bandpass(src, 750, 1050) * 0.5
        out += voice * np.sin(np.pi * np.clip((t - rng.uniform(0, 0.08)) / d, 0, 1)) ** 1.5
    return fade(normalize(out, 0.7), 0.02, 0.1)


def sfx_drama_down():
    parts = []
    for i in range(7):
        n = 84 + [0, 4, 7, 11, 12, 16, 19][i]
        parts.append((bell(midi(n), 0.4, 4.0, 1.2), i * 0.04, 0.3))
    return reverb(mix(*parts), 0.3, 1.0)


def sfx_love():
    kiss = np.sin(sweep(1800, 700, 0.05)) * env_ad(int(0.05 * SR), 0.001, 0.04)
    a = bell(midi(81), 0.6, 2.0, 0.8)
    b = bell(midi(88), 0.7, 2.0, 0.8)
    return reverb(mix((kiss, 0, 0.4), (a, 0.05, 0.35), (b, 0.16, 0.35)), 0.25, 1.0)


def sfx_fight():
    """Cartoon dust-cloud scuffle."""
    parts = []
    tcur = 0.0
    for i in range(9):
        kind = rng.integers(0, 3)
        if kind == 0:   # bonk
            d = 0.08
            x = np.sin(sweep(rng.uniform(500, 800), 180, d)) * env_ad(int(d * SR), 0.001, 0.06)
        elif kind == 1:  # swish
            d = 0.12
            x = bandpass(noise(d), 1200, 5000) * np.sin(np.linspace(0, np.pi, int(d * SR)))
        else:            # thud
            d = 0.1
            x = lowpass(noise(d), 500) * env_ad(int(d * SR), 0.001, 0.07) * 1.5
        parts.append((x, tcur, 0.45))
        tcur += rng.uniform(0.05, 0.11)
    return reverb(mix(*parts), 0.15, 0.6)


def sfx_splat():
    d = 0.35
    body = lowpass(noise(d), 900) * env_ad(int(d * SR), 0.002, 0.2)
    squish = np.sin(sweep(260, 80, 0.2)) * env_ad(int(0.2 * SR), 0.002, 0.15)
    return mix((body, 0, 0.8), (squish, 0, 0.6))


def sfx_gift_tick():
    d = 0.07
    wood = np.sin(2 * np.pi * 1350 * t_axis(d)) * env_ad(int(d * SR), 0.001, 0.035)
    wood += 0.4 * np.sin(2 * np.pi * 2650 * t_axis(d)) * env_ad(int(d * SR), 0.001, 0.02)
    return wood * 0.5


def sfx_gift_open():
    pop = np.sin(sweep(900, 200, 0.06)) * env_ad(int(0.06 * SR), 0.001, 0.05)
    crack = highpass(noise(0.02), 2000) * env_ad(int(0.02 * SR), 0.001, 0.015)
    sparkle = [(bell(midi(rng.integers(88, 101)), 0.25, 4.0, 1.0), 0.05 + i * 0.03, 0.12) for i in range(16)]
    fan = [(bell(midi(n), 0.9, 3.0, 1.4), 0.1 + i * 0.06, 0.32) for i, n in enumerate([77, 81, 84, 89])]
    return reverb(mix((pop, 0, 0.8), (crack, 0, 0.7), *sparkle, *fan), 0.3, 1.2)


def sfx_ribbon_cut():
    def snip():
        return highpass(noise(0.015), 3500) * env_ad(int(0.015 * SR), 0.0005, 0.01)
    d = 0.45
    sw = bandpass(noise(d), 500, 4000)
    sw *= np.sin(np.linspace(0, np.pi, len(sw))) ** 2
    rise = bell(midi(79), 0.6, 2.5, 1.0) * 0.25
    return reverb(mix((snip(), 0, 0.9), (snip(), 0.07, 0.9), (sw, 0.1, 0.35), (rise, 0.12, 1)), 0.2, 0.8)


def sfx_coin():
    a = lowpass(soft_square(midi(83), 0.07), 5000) * env_ad(int(0.07 * SR), 0.001, 0.06)
    b = lowpass(soft_square(midi(88), 0.35), 5000) * env_ad(int(0.35 * SR), 0.001, 0.3)
    return mix((a, 0, 0.25), (b, 0.07, 0.25))


def sfx_bouncer():
    d = 0.45
    t = t_axis(d)
    f = 2100 + 120 * np.sin(2 * np.pi * 28 * t)
    x = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.minimum(1, t / 0.02) * np.exp(-2.5 * t)
    breath = bandpass(noise(d), 1800, 3000) * 0.15 * np.exp(-3 * t)
    return (x * 0.35 + breath)


def sfx_champagne():
    pop = np.sin(sweep(700, 150, 0.05)) * env_ad(int(0.05 * SR), 0.0005, 0.04)
    crack = bandpass(noise(0.03), 1000, 6000) * env_ad(int(0.03 * SR), 0.0005, 0.02)
    d = 1.2
    fizz = highpass(noise(d), 4000) * np.exp(-2.4 * t_axis(d))
    crackle = np.zeros(int(d * SR))
    for _ in range(140):
        i = int(rng.uniform(0, d * 0.9) * SR)
        crackle[i:i + 30] += rng.uniform(-1, 1, 30) * np.exp(-np.arange(30) / 6)
    crackle *= np.exp(-2 * t_axis(d))
    return mix((pop, 0, 0.9), (crack, 0, 0.6), (fizz, 0.04, 0.12), (crackle, 0.04, 0.25))


def sfx_emote():
    d = 0.06
    x = np.sin(sweep(1300, 1900, d)) * env_ad(int(d * SR), 0.002, 0.05)
    return x * 0.35


def sfx_level_start():
    a = bell(midi(77), 1.6, 3.5, 2.0)
    b = bell(midi(84), 1.8, 3.5, 2.0)
    c = bell(midi(81), 2.0, 3.5, 2.0)
    return reverb(mix((a, 0, 0.35), (b, 0.22, 0.32), (c, 0.44, 0.3)), 0.35, 1.6)


def brass(f, dur, voices=3, bright=1.0):
    """Warm synth brass: detuned saws through a filter that opens on the attack (the 'blat')."""
    t = t_axis(dur)
    x = np.zeros(len(t))
    for k in range(voices):
        det = 1 + (k - (voices - 1) / 2) * 0.006
        vib = 1 + 0.004 * np.sin(2 * np.pi * 5.2 * t + k) * np.clip((t - 0.15) / 0.3, 0, 1)
        ph = 2 * np.pi * np.cumsum(f * det * vib) / SR
        x += sum(np.sin(h * ph) / h for h in range(1, 14))
    x /= voices
    # time-varying low-pass: run in short blocks with a falling cutoff
    cut = (900 + 3200 * bright * np.exp(-t / 0.06) + 1500 * bright) 
    out = np.zeros_like(x)
    blk = 256
    zi = None
    for i in range(0, len(x), blk):
        fc = float(min(cut[i], SR / 2 - 200))
        b, a = signal.butter(2, fc / (SR / 2), "low")
        if zi is None:
            zi = signal.lfilter_zi(b, a) * 0
        seg, zi = signal.lfilter(b, a, x[i:i + blk], zi=zi)
        out[i:i + blk] = seg
    amp = np.minimum(1, t / 0.025) * (0.8 + 0.2 * np.exp(-t / 0.08)) * np.minimum(1, (dur - t) / 0.08)
    return out * amp


def tubular(f, dur=3.0):
    """Church / tubular bell: inharmonic partials, long ring, bright strike."""
    t = t_axis(dur)
    parts = [(0.5, 0.6, 3.0), (1.0, 1.0, 2.6), (2.0, 0.55, 1.8), (3.0, 0.4, 1.3), (4.2, 0.3, 0.9), (5.4, 0.22, 0.6), (6.8, 0.15, 0.4)]
    x = np.zeros(len(t))
    for ratio, amp, dec in parts:
        x += amp * np.sin(2 * np.pi * f * ratio * t + ratio) * np.exp(-t / dec)
    strike = highpass(noise(0.01), 2500) * 0.3
    x[:len(strike)] += strike
    return fade(x, 0.001, 0.05)


def sfx_win():
    """Wedding victory: harp glissando -> trumpet fanfare -> big major chord with church bells,
    sparkles and the guests applauding."""
    parts = []
    # 1) harp glissando up the C major scale
    gl = [60, 62, 64, 67, 69, 72, 74, 76, 79, 81, 84, 86, 88, 91]
    for i, n in enumerate(gl):
        parts.append((pluck(midi(n), 0.9, 0.9), i * 0.028, 0.32))
        parts.append((bell(midi(n + 12), 0.4, 2.0, 0.5), i * 0.028, 0.05))
    # 2) fanfare: "ta-ta-ta TAA, ta-ta-ta TAAA" (wedding-march style triplets)
    t0, trip = 0.46, 0.095
    seq = [(72, trip), (72, trip), (72, trip), (76, 0.24), (76, trip), (76, trip), (76, trip), (79, 0.34)]
    s = t0
    for n, d in seq:
        parts.append((brass(midi(n), d + 0.08), s, 0.42))
        parts.append((brass(midi(n - 12), d + 0.08, 2, 0.6), s, 0.22))
        parts.append((bell(midi(n + 12), 0.5, 2.0, 0.6), s, 0.06))
        s += d
    hit = s + 0.02
    # 3) the big C major chord: brass section + warm pad + timpani + cymbal swell
    for n, g in [(48, 0.3), (60, 0.34), (64, 0.3), (67, 0.3), (72, 0.32), (76, 0.24)]:
        parts.append((brass(midi(n), 1.9, 3, 0.9), hit, g))
    parts.append((pad([midi(60), midi(64), midi(67), midi(72)], 2.0), hit, 0.45))
    timp = np.sin(sweep(98, 62, 0.9)) * env_ad(int(0.9 * SR), 0.003, 0.6)
    parts.append((timp, hit, 0.6))
    parts.append((timp, t0 - 0.02, 0.35))
    cym = highpass(noise(2.2), 5000) * np.exp(-t_axis(2.2) / 0.6)
    parts.append((cym, hit, 0.10))
    # 4) church bells pealing down the chord, twice
    for k, n in enumerate([84, 79, 76, 72, 84, 79, 76, 72]):
        parts.append((tubular(midi(n), 1.8), hit + 0.08 + k * 0.24, 0.22 if k < 4 else 0.15))
    # 5) sparkles and applause
    for i in range(18):
        parts.append((bell(midi(int(rng.integers(91, 103))), 0.3, 4.0, 1.0), hit + 0.05 + i * 0.07, 0.07))
    parts.append((sfx_cheer(), hit + 0.1, 0.55))
    x = reverb(mix(*parts), 0.28, 1.6, 4500)
    # keep it snappy: ~4.3 s with a gentle fade over the last second
    end = int(4.3 * SR)
    x = x[:end]
    x[-SR:] *= np.linspace(1, 0, SR) ** 1.5
    return x


WIN_HIT = 0.46 + 0.095 * 6 + 0.24 + 0.34 + 0.02   # when the big chord lands (Sfx shows the popup then)


def sfx_lose():
    """Sad 'wah-wah-wah-waaah' (muted brass)."""
    parts = []
    seq = [(midi(55), 0.32), (midi(54), 0.32), (midi(53), 0.32), (midi(52), 1.1)]
    s = 0.0
    for i, (f, d) in enumerate(seq):
        t = t_axis(d)
        vib = 1 + (0.03 * np.sin(2 * np.pi * 5.5 * t) * np.clip(t / 0.3, 0, 1) if i == 3 else np.zeros_like(t))
        ph = 2 * np.pi * np.cumsum(f * vib) / SR
        x = sum(np.sin(k * ph) / k ** 1.2 for k in range(1, 12))
        wah = 0.5 + 0.5 * np.sin(np.pi * np.clip(t / (d * 0.8), 0, 1))
        x = lowpass(x, 900, 2) * wah * np.minimum(1, (d - t) / 0.05)
        parts.append((x, s, 0.4))
        s += d + 0.04
    return reverb(mix(*parts), 0.2, 1.0)


def amb_crowd():
    """Seamless 12 s murmur loop (very quiet in game)."""
    d = 12.0
    t = t_axis(d)
    out = np.zeros(len(t))
    for v in range(14):
        src = bandpass(noise(d), 250, 1600, 2)
        lfo = 0.5 + 0.5 * np.sin(2 * np.pi * (rng.integers(1, 6) / d) * t + rng.uniform(0, 6.28))
        syll = 0.5 + 0.5 * np.sin(2 * np.pi * (rng.integers(30, 60) / d) * t + rng.uniform(0, 6.28))
        out += src * lfo * syll ** 2
    out = lowpass(out, 1800, 2)
    # make it loop: crossfade the tail into the head
    xf = int(1.0 * SR)
    head, tail = out[:xf].copy(), out[-xf:].copy()
    w = np.linspace(0, 1, xf)
    out[:xf] = head * w + tail * (1 - w)
    out = out[:-xf]
    return normalize(out, 0.5)


# ---------------------------------------------------------------- music

BPM = 138          # 3/4 waltz
BEAT = 60.0 / BPM
BAR = 3 * BEAT

CHORDS = {
    "F": (41, [57, 60, 65]), "C": (48, [55, 60, 64]), "Dm": (50, [57, 62, 65]),
    "Bb": (46, [58, 62, 65]), "Gm": (43, [58, 62, 67]), "Am": (45, [57, 60, 64]),
    "C7": (48, [58, 60, 64]),
}

A1 = [("F", [(69, 1), (72, 1), (77, 1)]),
      ("C", [(76, 2), (74, .5), (72, .5)]),
      ("Dm", [(74, 1), (77, 1), (81, 1)]),
      ("Bb", [(79, 2), (77, 1)]),
      ("F", [(81, 1), (79, .5), (77, .5), (72, 1)]),
      ("Gm", [(70, 1), (74, 1), (79, 1)]),
      ("C", [(77, 1), (76, 1), (79, 1)]),
      ("F", [(77, 3)])]
A2 = A1[:6] + [("C", [(76, 1), (74, 1), (76, 1)]), ("F", [(77, 2), (None, 1)])]
B = [("Bb", [(74, 1), (77, 1), (82, 1)]),
     ("F", [(81, 2), (77, 1)]),
     ("Gm", [(79, 1), (81, .5), (79, .5), (77, 1)]),
     ("C", [(76, 2), (72, 1)]),
     ("Am", [(72, 1), (76, 1), (81, 1)]),
     ("Dm", [(77, 1), (76, .5), (74, .5), (69, 1)]),
     ("Gm", [(70, 1), (74, 1), (79, 1)]),
     ("C7", [(76, 1), (79, 1), (82, 1)])]
SONG = A1 + A2 + B + A2


def music():
    total = len(SONG) * BAR
    tail = 2.0
    n = int((total + tail) * SR)
    mel = np.zeros(n)
    acc = np.zeros(n)
    bass = np.zeros(n)
    perc = np.zeros(n)
    padbus = np.zeros(n)

    def put(buf, x, start):
        i = int(start * SR)
        j = min(n, i + len(x))
        buf[i:j] += x[: j - i]

    for b, (ch, notes) in enumerate(SONG):
        t0 = b * BAR
        root, triad = CHORDS[ch]
        put(bass, pluck(midi(root), 1.0, 0.2) * 0.9 + sine(midi(root), 1.0) * env_ad(int(SR), 0.01, 0.6) * 0.5, t0)
        for beat in (1, 2):
            for k, nn in enumerate(triad):
                put(acc, pluck(midi(nn), 0.5, 0.6) * (0.3 if beat == 1 else 0.24), t0 + beat * BEAT + k * 0.006)
        put(padbus, pad([midi(x) for x in triad], BAR + 0.15), t0)
        s = t0
        for nn, beats in notes:
            d = beats * BEAT
            if nn is not None:
                put(mel, bell(midi(nn), max(0.5, d + 0.5), 3.5, 1.6) * 0.55, s)
                put(mel, bell(midi(nn + 12), 0.4, 2.0, 0.6) * 0.08, s)
            s += d
        # light shaker on beats 2 and 3, a soft tick on 1
        for beat in range(3):
            sh = highpass(noise(0.05), 6000) * env_ad(int(0.05 * SR), 0.003, 0.03)
            put(perc, sh * (0.12 if beat else 0.18), t0 + beat * BEAT)

    song = mel * 0.9 + acc * 0.7 + bass * 0.75 + padbus * 0.35 + perc
    song = reverb(song, 0.22, 1.6)[:n]
    # seamless loop: fold the reverb tail back over the start
    L = int(total * SR)
    loop = song[:L].copy()
    tl = song[L:L + int(tail * SR)]
    loop[: len(tl)] += tl
    return normalize(lowpass(loop, 9000, 1), 0.8)


# ---------------------------------------------------------------- output

SFX = {
    "ui_tap": sfx_ui_tap, "select": sfx_select, "lift": sfx_lift, "deny": sfx_deny,
    "seat": sfx_seat, "bench": sfx_bench, "table_done": sfx_table_done, "cheer": sfx_cheer,
    "drama_up": sfx_drama_up, "drama_big": sfx_drama_big, "gasp": sfx_gasp, "drama_down": sfx_drama_down,
    "love": sfx_love, "fight": sfx_fight, "splat": sfx_splat, "gift_tick": sfx_gift_tick,
    "gift_open": sfx_gift_open, "ribbon_cut": sfx_ribbon_cut, "coin": sfx_coin,
    "bouncer": sfx_bouncer, "champagne": sfx_champagne, "emote": sfx_emote,
    "level_start": sfx_level_start, "win": sfx_win, "lose": sfx_lose,
}


def write_wav(path, x, sr=SR):
    from scipy.io import wavfile
    x = np.clip(x, -1, 1)
    wavfile.write(path, sr, (x * 32767).astype(np.int16))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "out"
    os.makedirs(out, exist_ok=True)
    jobs = {k: (lambda f=f: loudness(trim_tail(fade(f(), 0.001, 0.01)))) for k, f in SFX.items()}
    jobs["amb_crowd"] = amb_crowd
    jobs["music_main"] = music
    for name, fn in jobs.items():
        x = fn()
        wav = os.path.join(out, name + ".wav")
        write_wav(wav, x)
        print(f"{name:12s} {len(x) / SR:6.2f}s")
        try:
            q = "4" if name.startswith("music") or name.startswith("amb") else "5"
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", wav, "-ac", "1", "-c:a", "libvorbis", "-q:a", q,
                            os.path.join(out, name + ".ogg")], check=True)
        except (OSError, subprocess.CalledProcessError) as e:
            print("  (ogg skipped:", e, ")")


if __name__ == "__main__":
    main()
