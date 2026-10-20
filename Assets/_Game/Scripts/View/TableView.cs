using System.Collections;
using UnityEngine;
using WeddingChaos.Core;

namespace WeddingChaos.View
{
    /// <summary>Clickable seat marker on a chair.</summary>
    public class SeatTarget : MonoBehaviour
    {
        public TableView Table;
        public int Seat;
    }

    /// <summary>Round wedding table with 4 chairs. Seat k: 0 far, 1 right, 2 near (star seat, faces the stage), 3 left.</summary>
    public class TableView : MonoBehaviour
    {
        public char Color;
        public readonly Transform[] Anchors = new Transform[Table.SeatCount];
        public readonly GuestView[] Seated = new GuestView[Table.SeatCount];
        readonly Renderer[] markers = new Renderer[Table.SeatCount];
        Transform top;
        GameObject lockMark;

        static readonly Vector3[] Dirs = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
        const float SeatRadius = 1.05f;

        public static TableView Create(char color, Vector3 position, Transform parent, bool popIn = false)
        {
            var go = new GameObject("Table " + color);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var tv = go.AddComponent<TableView>();
            tv.Color = color;
            tv.Build();
            if (popIn) tv.StartCoroutine(tv.PopIn());
            return tv;
        }

        void Build()
        {
            Color c = Palette.Invite(Color);
            top = Palette.Pivot(transform, "Top", Vector3.zero);
            // long cloth in the invitation colour, white topper, gold-and-flower centrepiece with two candles
            Palette.Prim(PrimitiveType.Cylinder, top, new Vector3(0, 0.28f, 0), new Vector3(1.45f, 0.28f, 1.45f), c, name: "Skirt");
            MeshFactory.Make(MeshFactory.Frustum(0.74f, 0.8f, 0.14f), top, new Vector3(0, 0.46f, 0), Vector3.one, Palette.Cloth, "Topper");
            Palette.Prim(PrimitiveType.Cylinder, top, new Vector3(0, 0.6f, 0), new Vector3(1.5f, 0.02f, 1.5f), Palette.Cloth, name: "Cloth");
            Palette.Prim(PrimitiveType.Cylinder, top, new Vector3(0, 0.66f, 0), new Vector3(0.2f, 0.06f, 0.2f), Palette.Gold, name: "Stand");
            var bouquet = new[] { (new Vector3(0, 0.84f, 0), 0.24f, Palette.Flower), (new Vector3(0.13f, 0.8f, 0.06f), 0.17f, UnityEngine.Color.white),
                                  (new Vector3(-0.12f, 0.8f, -0.05f), 0.17f, Palette.Blush), (new Vector3(0.02f, 0.78f, 0.14f), 0.13f, Palette.Leaf),
                                  (new Vector3(-0.04f, 0.78f, -0.15f), 0.13f, Palette.Leaf) };
            foreach (var (p, sz, col) in bouquet) Palette.Prim(PrimitiveType.Sphere, top, p, Vector3.one * sz, col, name: "Flower");
            foreach (var x in new[] { -0.36f, 0.36f })
            {
                Palette.Prim(PrimitiveType.Cylinder, top, new Vector3(x, 0.72f, 0.08f), new Vector3(0.07f, 0.1f, 0.07f), Palette.Cloth, name: "Candle");
                Palette.Prim(PrimitiveType.Sphere, top, new Vector3(x, 0.86f, 0.08f), new Vector3(0.05f, 0.08f, 0.05f), Palette.Bulb, name: "Flame");
            }

            for (int k = 0; k < Table.SeatCount; k++)
            {
                Vector3 dir = Dirs[k];
                var anchor = Palette.Pivot(transform, "Seat" + k, dir * SeatRadius);
                anchor.rotation = Quaternion.LookRotation(-dir);      // sit facing the table
                Anchors[k] = anchor;
                var chair = Palette.Pivot(anchor, "Chair", Vector3.zero);
                // banquet chair in a white cover with a blush sash and bow on the back
                MeshFactory.Make(MeshFactory.Frustum(0.25f, 0.29f, 0.3f), chair, new Vector3(0, 0.02f, 0), Vector3.one, Palette.Chair, "CoverSkirt");
                Palette.Prim(PrimitiveType.Cube, chair, new Vector3(0, 0.33f, 0), new Vector3(0.52f, 0.07f, 0.48f), Palette.Chair);
                Palette.Prim(PrimitiveType.Cube, chair, new Vector3(0, 0.66f, -0.24f), new Vector3(0.5f, 0.62f, 0.09f), Palette.Chair);
                Palette.Prim(PrimitiveType.Sphere, chair, new Vector3(0, 0.97f, -0.24f), new Vector3(0.5f, 0.12f, 0.09f), Palette.Chair);
                Palette.Prim(PrimitiveType.Cube, chair, new Vector3(0, 0.6f, -0.24f), new Vector3(0.52f, 0.09f, 0.11f), Palette.Sash, name: "Sash");
                Palette.Prim(PrimitiveType.Sphere, chair, new Vector3(-0.08f, 0.6f, -0.31f), new Vector3(0.14f, 0.11f, 0.05f), Palette.Sash, name: "BowL");
                Palette.Prim(PrimitiveType.Sphere, chair, new Vector3(0.08f, 0.6f, -0.31f), new Vector3(0.14f, 0.11f, 0.05f), Palette.Sash, name: "BowR");
                var marker = Palette.Prim(PrimitiveType.Cylinder, anchor, new Vector3(0, 0.38f, 0.02f), new Vector3(0.42f, 0.01f, 0.42f),
                    k == Table.StarSeat ? Palette.Gold : Palette.Cloth, keepCollider: true, name: "Marker");
                var box = marker.GetComponent<Collider>();
                if (box) Destroy(box);
                var trigger = marker.AddComponent<BoxCollider>();
                trigger.size = new Vector3(2.2f, 80f, 2.2f);           // generous tap target (scaled by the flat marker)
                var st = marker.AddComponent<SeatTarget>();
                st.Table = this; st.Seat = k;
                markers[k] = marker.GetComponent<Renderer>();
                marker.SetActive(true);
            }
            lockMark = Palette.Prim(PrimitiveType.Sphere, transform, new Vector3(0, 1.4f, 0), new Vector3(0.5f, 0.35f, 0.5f), Palette.Hex("#F4D6C4"), name: "Cake");
            Palette.Prim(PrimitiveType.Sphere, lockMark.transform, new Vector3(0, 0.6f, 0), Vector3.one * 0.35f, Palette.Accent);
            lockMark.SetActive(false);
        }

        IEnumerator PopIn()
        {
            for (float t = 0; t < 0.35f; t += Time.deltaTime)
            {
                float k = t / 0.35f;
                transform.localScale = Vector3.one * (k < 0.7f ? Mathf.Lerp(0.2f, 1.12f, k / 0.7f) : Mathf.Lerp(1.12f, 1f, (k - 0.7f) / 0.3f));
                yield return null;
            }
            transform.localScale = Vector3.one;
        }

        public void SetHighlight(int seat, bool on, int delta)
        {
            var r = markers[seat];
            if (r == null) return;
            Color baseC = seat == Table.StarSeat ? Palette.Gold : Palette.Cloth;
            Color c = !on ? baseC : delta > 0 ? Palette.Hex("#F7A1A1") : delta < 0 ? Palette.Hex("#A8E6C8") : Palette.Hex("#FFE07A");
            r.sharedMaterial = Palette.Mat(c);
            r.transform.localScale = on ? new Vector3(0.6f, 0.02f, 0.6f) : new Vector3(0.42f, 0.01f, 0.42f);
        }

        public void ClearHighlights() { for (int k = 0; k < Table.SeatCount; k++) SetHighlight(k, false, 0); }

        public void SetLocked(bool locked) { lockMark.SetActive(locked); }

        public Vector3 ApproachPoint(int seat) => Anchors[seat].position - Anchors[seat].forward * 0.75f;

        /// <summary>Everyone cheers, the table bounces and shrinks away with its guests.</summary>
        public IEnumerator Celebrate()
        {
            foreach (var g in Seated) if (g) g.SetPose(GuestView.Pose.Cheer);
            yield return new WaitForSeconds(0.9f);
            for (float t = 0; t < 0.45f; t += Time.deltaTime)
            {
                float k = t / 0.45f;
                transform.localScale = Vector3.one * (k < 0.3f ? 1 + k * 0.5f : Mathf.Lerp(1.15f, 0f, (k - 0.3f) / 0.7f));
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
