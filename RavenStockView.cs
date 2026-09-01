// 運送レイヴンと荷下ろし地点にカーソルを合わせている間だけ、
// 手元にある荷を「絵 × 個数」で並べて出す。
//
// -- なぜ要るか ---------------------------------------------------------
// 荷下ろし地点は 1000 セル × 100 個の受け皿で、詰まっても見た目が変わらない。
// 本体はホバー時の情報窓に毎秒の流量（TransporterItemPerSecProvider）は出すが、
// 「今なにがいくつ積み上がっているか」はどこにも出ない。
// 線のどこで詰まっているのか、何が滞っているのかは、中身を見るのが一番早い。
//
// 常時表示も試したが、数字が盤面じゅうに浮いて邪魔になった。指しているものだけ出す。
//
// マップビュー（俯瞰）でもホバーは同じに効かせる。全体の札（各物のマスの上に
// 「絵 × 個数」）は普段は出さず、再投下の札の隣に置いた「在庫」ボタンで切り替える。
// 常時表示・下側・中央置きと試したが、どれも隣のマスと喧嘩して読めなかった。
// 見たい瞬間だけ出すのが結論。
//
// -- 取り方 -------------------------------------------------------------
// カーソルの下のマスを MapObjectDictionary から引き、レイヴンか荷下ろし地点なら
// FamObject.GetInventory() の InventoryDB.StoragedItems をアイテムごとに合計する。
// レイヴンは運搬中の荷（GetDeliveryInventory）も足す。ゲームの挙動には一切触らない。
// 見るのは1体だけなので毎フレーム数え直してよい（セルは多くて 1000）。
//
// -- 絵の描き方 ---------------------------------------------------------
// スプライトは余白を切り詰めてアトラスに詰められていることがあり、textureRect が
// 論理サイズ（sprite.rect）より小さい。切り詰め後の絵を枠いっぱいに伸ばすと
// 縦横比も位置も崩れるので、論理サイズを基準に縮尺を決めてその中へ置く。

using System.Collections.Generic;
using Items;
using Items.Inventory;
using Items.ValueCountSettings;
using Map;
using Map.GridManagerHelpers;
using Input.PlayerAction;
using Map.MapObjects.PureClass;
using Map.MapObjects.PureClass.FamObjects;
using UI;
using UI.Cursor;
using UnityEngine;
using UnityEngine.UI;

namespace LwfRavenQol
{
    /// <summary>指しているレイヴン／荷下ろし地点の中身を、その場に重ねて出す。</summary>
    internal static class StockView
    {
        private struct Row
        {
            public string ItemID;
            public int Count;
        }

        private const int MaxRows = 8;

        /// <summary>マップビューで重ねる1件。位置は描く瞬間にカメラから引き直す。</summary>
        private sealed class OverlayEntry
        {
            public Transform Anchor;
            public readonly List<Row> Rows = new List<Row>();
            public int HiddenKinds;
        }

        // 俯瞰の札は小さいので、内訳はホバーより絞る
        private const int MaxOverlayRows = 4;

        private static readonly List<OverlayEntry> OverlayEntries = new List<OverlayEntry>();
        private const float OverlayScanInterval = 0.25f;
        private static float _nextOverlayScan = float.NegativeInfinity;

        // IMGUI は常に uGUI より上に描かれるので、UI が居る場所の札は描かずに避ける。
        // 見えている HUD 部品の画面矩形を札の走査と同じ間隔で集めておく
        private static readonly List<Rect> UiBlockers = new List<Rect>();
        private static readonly Vector3[] CornerBuffer = new Vector3[4];
        private static GUIStyle _overlayStyle;
        private static bool _overlayOn;

        /// <summary>マップビューの全体表示。切り替えボタン（HudTweaks が仕立てる札）から突かれる。</summary>
        internal static bool MapOverlayOn
        {
            get { return _overlayOn; }
            set
            {
                if (_overlayOn == value) { return; }
                _overlayOn = value;
                RavenQolPlugin.Log.LogInfo("[stock] map-view stock overlay " + (value ? "on" : "off"));
            }
        }

        private static readonly List<Row> Rows = new List<Row>();
        private static readonly Dictionary<string, int> Totals = new Dictionary<string, int>();

        private static Vector3 _anchorWorld;
        private static bool _hasTarget;
        private static int _hiddenKinds;

        private static bool _visible = true;   // 設定「出す」の写し。毎フレーム読む
        private static bool _initialized;


        private static GUIStyle _countStyle;
        private static Texture2D _panelTexture;
        private static readonly GUIContent Content = new GUIContent();

        private static readonly Color PanelColor = new Color(0.06f, 0.05f, 0.08f, 0.86f);
        private static readonly Color EdgeColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color TextColor = new Color(1f, 0.97f, 0.9f, 1f);
        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.9f);

        internal static void Update()
        {
            if (!_initialized)
            {
                _overlayOn = RavenQolPlugin.StockMapOverlay.Value;
                _initialized = true;
            }
            _visible = RavenQolPlugin.StockVisible.Value;

            _hasTarget = false;
            Rows.Clear();
            _hiddenKinds = 0;
            if (!_visible) { OverlayEntries.Clear(); return; }

            if (HudTweaks.IsMapViewActive() && _overlayOn)
            {
                ScanOverlay();
            }
            else
            {
                OverlayEntries.Clear();
            }
            Scan();
        }

        /// <summary>画面内のレイヴンと荷下ろし地点を全部数える。合計は間隔を置いて取り直す。</summary>
        private static void ScanOverlay()
        {
            float now = Time.unscaledTime;
            if (now < _nextOverlayScan) { return; }
            _nextOverlayScan = now + OverlayScanInterval;

            OverlayEntries.Clear();
            RefreshUiBlockers();

            CursorHitChecker hit = Chain.CurrentCursorHitChecker();
            if (hit == null || hit.GridManager == null) { return; }
            MapObjectDictionary dict = hit.GridManager.GetMapObjectDictionary();
            if (dict == null) { return; }
            Camera camera = CurrentCamera();
            if (camera == null) { return; }

            foreach (KeyValuePair<Vector2Int, ConnectableObject> pair in dict.ConnectableDic)
            {
                ConnectableObject obj = pair.Value;
                if (obj == null || !obj.pivot) { continue; }

                TransporterExitObject exit = obj as TransporterExitObject;
                TransporterObject raven = ((exit == null) ? (obj as TransporterObject) : null);
                if (exit == null && raven == null) { continue; }

                Transform anchor = obj.pivot.transform;

                // 画面の外は数えない（荷下ろし地点は 1000 セルあるので、無駄に舐めない）
                Vector3 screen = camera.WorldToScreenPoint(anchor.position);
                if (screen.z <= 0f) { continue; }
                if (screen.x < -80f || screen.x > Screen.width + 80f) { continue; }
                if (screen.y < -80f || screen.y > Screen.height + 80f) { continue; }

                Totals.Clear();
                if (exit != null)
                {
                    Gather(exit.GetInventory());
                }
                else
                {
                    Gather(raven.GetInventory());
                    Gather(raven.GetDeliveryInventory());
                }
                int total = 0;
                foreach (KeyValuePair<string, int> sum in Totals) { total += sum.Value; }
                if (total < 1) { continue; }

                OverlayEntry entry = new OverlayEntry();
                entry.Anchor = anchor;
                foreach (KeyValuePair<string, int> sum in Totals)
                {
                    Row row = default(Row);
                    row.ItemID = sum.Key;
                    row.Count = sum.Value;
                    entry.Rows.Add(row);
                }
                entry.Rows.Sort(CompareByCountDescending);
                if (entry.Rows.Count > MaxOverlayRows)
                {
                    entry.HiddenKinds = entry.Rows.Count - MaxOverlayRows;
                    entry.Rows.RemoveRange(MaxOverlayRows, entry.HiddenKinds);
                }
                OverlayEntries.Add(entry);
            }
        }

        private static void Scan()
        {
            // マップビューでは盤面を触れる状態の判定が別物なので、UI の上かどうかだけ見る。
            // 通常画面はこれまでどおり（メニューや窓が開いている間は出さない）
            if (HudTweaks.IsMapViewActive())
            {
                if (PointerOverFinder.IsPointerOverUI) { return; }
            }
            else if (!Chain.IsMapInteractive()) { return; }

            CursorHitChecker hit = Chain.CurrentCursorHitChecker();
            if (hit == null) { return; }
            ChildGrid grid = hit.HitChildGrid;
            if (grid == null) { return; }

            MapObject target = hit.GetAdjustedMapObject(grid.GlobalAddress);
            TransporterExitObject exit = target as TransporterExitObject;
            TransporterObject raven = ((exit == null) ? (target as TransporterObject) : null);
            if (exit == null && raven == null) { return; }
            if (!target.pivot) { return; }

            Totals.Clear();
            if (exit != null)
            {
                Gather(exit.GetInventory());
            }
            else
            {
                Gather(raven.GetInventory());
                Gather(raven.GetDeliveryInventory());
            }

            foreach (KeyValuePair<string, int> pair in Totals)
            {
                Row row = default(Row);
                row.ItemID = pair.Key;
                row.Count = pair.Value;
                Rows.Add(row);
            }
            Rows.Sort(CompareByCountDescending);

            if (Rows.Count > MaxRows)
            {
                _hiddenKinds = Rows.Count - MaxRows;
                Rows.RemoveRange(MaxRows, _hiddenKinds);
            }

            _anchorWorld = target.pivot.transform.position;
            _hasTarget = true;
        }

        private static int CompareByCountDescending(Row a, Row b)
        {
            if (a.Count != b.Count) { return b.Count.CompareTo(a.Count); }
            return string.CompareOrdinal(a.ItemID, b.ItemID);
        }

        private static void Gather(InventoryManager inventory)
        {
            if (inventory == null) { return; }
            InventoryDB db = inventory.GetDB();
            if (db == null) { return; }

            foreach (KeyValuePair<int, ItemCount> cell in db.StoragedItems)
            {
                ItemCount item = cell.Value;
                if (item == null || item.Count <= 0) { continue; }
                string id = item.ItemID;
                if (string.IsNullOrEmpty(id) || id == "None") { continue; }

                int already;
                Totals[id] = (Totals.TryGetValue(id, out already) ? already : 0) + item.Count;
            }
        }

        internal static void Draw()
        {
            if (!_visible) { return; }
            if (Event.current.type != EventType.Repaint) { return; }

            if (OverlayEntries.Count > 0) { DrawOverlay(); }

            if (!_hasTarget) { return; }
            Camera camera = CurrentCamera();
            if (camera == null) { return; }
            Vector3 screen = camera.WorldToScreenPoint(_anchorWorld);
            if (screen.z <= 0f) { return; }

            EnsureResources();

            float icon = Mathf.Clamp(Screen.height * 0.026f, 16f, 40f);
            float gap = Mathf.Round(icon * 0.25f);
            float pad = Mathf.Round(icon * 0.30f);
            float rowHeight = icon;

            // 一番長い数字に幅を合わせる。詰めすぎると末尾が切れる。
            float numberWidth = 0f;
            for (int i = 0; i < Rows.Count; i++)
            {
                Content.text = Rows[i].Count.ToString();
                numberWidth = Mathf.Max(numberWidth, _countStyle.CalcSize(Content).x);
            }
            if (_hiddenKinds > 0)
            {
                Content.text = "+" + _hiddenKinds;
                numberWidth = Mathf.Max(numberWidth, _countStyle.CalcSize(Content).x);
            }

            int lines = Rows.Count + ((_hiddenKinds > 0) ? 1 : 0);
            if (lines <= 0) { return; }

            float width = pad * 2f + icon + gap + numberWidth;
            float height = pad * 2f + rowHeight * lines;

            float x = screen.x - width * 0.5f;
            float y = (float)Screen.height - screen.y - height - Screen.height * 0.035f;

            // 画面からはみ出さないよう寄せる。切れて読めないのが一番困る。
            x = Mathf.Clamp(x, 4f, Mathf.Max(4f, (float)Screen.width - width - 4f));
            y = Mathf.Clamp(y, 4f, Mathf.Max(4f, (float)Screen.height - height - 4f));

            Rect panel = new Rect(Mathf.Round(x), Mathf.Round(y), Mathf.Round(width), Mathf.Round(height));
            DrawPanel(panel);

            float rowY = panel.y + pad;
            for (int i = 0; i < Rows.Count; i++)
            {
                Rect iconRect = new Rect(panel.x + pad, rowY, icon, icon);
                DrawSprite(iconRect, ItemParamGetter.GetSprite(Rows[i].ItemID), Color.white);

                Rect numberRect = new Rect(iconRect.xMax + gap, rowY, numberWidth, rowHeight);
                DrawShadowedText(numberRect, _countStyle, Rows[i].Count.ToString(), TextColor);
                rowY += rowHeight;
            }
            if (_hiddenKinds > 0)
            {
                Rect numberRect = new Rect(panel.x + pad, rowY, panel.width - pad * 2f, rowHeight);
                DrawShadowedText(numberRect, _countStyle, "+" + _hiddenKinds, TextColor);
            }
        }

        /// <summary>見えている HUD 部品の画面矩形を集める。札はここに被る位置なら描かない。</summary>
        private static void RefreshUiBlockers()
        {
            UiBlockers.Clear();
            Canvas canvas = HudTweaks.MainCanvas();
            if (canvas == null) { return; }
            Camera uiCamera = ((canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null);

            Graphic[] graphics = canvas.GetComponentsInChildren<Graphic>(false);
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null || !graphic.enabled) { continue; }
                // CanvasGroup で消してあるもの（マップビュー中の操作説明など）は避けなくていい
                if (graphic.canvasRenderer == null || graphic.canvasRenderer.GetInheritedAlpha() < 0.05f) { continue; }
                if (graphic.color.a < 0.05f) { continue; }

                RectTransform rt = graphic.rectTransform;
                rt.GetWorldCorners(CornerBuffer);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int c = 0; c < 4; c++)
                {
                    Vector2 point = RectTransformUtility.WorldToScreenPoint(uiCamera, CornerBuffer[c]);
                    minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                    minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
                }
                if (maxX - minX <= 1f || maxY - minY <= 1f) { continue; }
                // 画面の何割も覆うものは、窓ではなく縁取りや背景の飾り。
                // 避ける対象にすると札が全部消えるので、窓の大きさのものだけ拾う
                float area = (maxX - minX) * (maxY - minY);
                if (area > (float)Screen.width * (float)Screen.height * 0.2f) { continue; }
                UiBlockers.Add(new Rect(minX, (float)Screen.height - maxY, maxX - minX, maxY - minY));
            }
        }

        private static bool IsCoveredByUi(Rect rect)
        {
            for (int i = 0; i < UiBlockers.Count; i++)
            {
                if (rect.Overlaps(UiBlockers[i])) { return true; }
            }
            return false;
        }

        /// <summary>
        /// マップビュー：画面内の全部に、ホバーと同じ書式（絵 × 個数）の小さな札を重ねる。
        /// 位置は毎フレーム引き直す。
        /// </summary>
        private static void DrawOverlay()
        {
            Camera camera = CurrentCamera();
            if (camera == null) { return; }
            EnsureResources();

            float icon = Mathf.Clamp(Screen.height * 0.020f, 12f, 30f);
            float gap = Mathf.Round(icon * 0.22f);
            float pad = Mathf.Round(icon * 0.22f);

            for (int i = 0; i < OverlayEntries.Count; i++)
            {
                OverlayEntry entry = OverlayEntries[i];
                if (entry.Anchor == null || !entry.Anchor || entry.Rows.Count == 0) { continue; }

                Vector3 screen = camera.WorldToScreenPoint(entry.Anchor.position);
                if (screen.z <= 0f) { continue; }

                float numberWidth = 0f;
                for (int r = 0; r < entry.Rows.Count; r++)
                {
                    Content.text = entry.Rows[r].Count.ToString();
                    numberWidth = Mathf.Max(numberWidth, _overlayStyle.CalcSize(Content).x);
                }
                if (entry.HiddenKinds > 0)
                {
                    Content.text = "+" + entry.HiddenKinds;
                    numberWidth = Mathf.Max(numberWidth, _overlayStyle.CalcSize(Content).x);
                }

                int lines = entry.Rows.Count + ((entry.HiddenKinds > 0) ? 1 : 0);
                float width = Mathf.Round(pad * 2f + icon + gap + numberWidth);
                float height = Mathf.Round(pad * 2f + icon * lines);
                // 見たいときだけ出す表示なので、マスの上に浮かべる（読みやすさ優先）
                float x = Mathf.Round(screen.x - width * 0.5f);
                float y = Mathf.Round((float)Screen.height - screen.y - height - Screen.height * 0.012f);
                if (x < -width || x > (float)Screen.width) { continue; }
                if (y < -height || y > (float)Screen.height) { continue; }

                Rect panel = new Rect(x, y, width, height);
                if (IsCoveredByUi(panel)) { continue; }
                DrawPanel(panel);

                float rowY = panel.y + pad;
                for (int r = 0; r < entry.Rows.Count; r++)
                {
                    DrawSprite(new Rect(panel.x + pad, rowY, icon, icon),
                        ItemParamGetter.GetSprite(entry.Rows[r].ItemID), Color.white);
                    DrawShadowedText(new Rect(panel.x + pad + icon + gap, rowY, numberWidth, icon),
                        _overlayStyle, entry.Rows[r].Count.ToString(), TextColor);
                    rowY += icon;
                }
                if (entry.HiddenKinds > 0)
                {
                    DrawShadowedText(new Rect(panel.x + pad, rowY, panel.width - pad * 2f, icon),
                        _overlayStyle, "+" + entry.HiddenKinds, TextColor);
                }
            }
        }

        private static void DrawPanel(Rect panel)
        {
            Color previous = GUI.color;
            GUI.color = PanelColor;
            GUI.DrawTexture(panel, _panelTexture);
            GUI.color = EdgeColor;
            GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), _panelTexture);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), _panelTexture);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), _panelTexture);
            GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), _panelTexture);
            GUI.color = previous;
        }

        private static void DrawShadowedText(Rect rect, GUIStyle style, string text, Color color)
        {
            Content.text = text;
            Color previous = GUI.color;
            GUI.color = ShadowColor;
            style.Draw(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), Content, false, false, false, false);
            GUI.color = color;
            style.Draw(rect, Content, false, false, false, false);
            GUI.color = previous;
        }

        /// <summary>
        /// アトラスの一部を切り出して描く。切り詰められたスプライトでも
        /// 縦横比と位置が崩れないよう、論理サイズ（sprite.rect）を基準に置く。
        /// </summary>
        private static void DrawSprite(Rect rect, Sprite sprite, Color tint)
        {
            if (sprite == null || sprite.texture == null) { return; }

            Rect tr = sprite.textureRect;
            float tw = sprite.texture.width;
            float th = sprite.texture.height;
            if (tw <= 0f || th <= 0f || tr.width <= 0f || tr.height <= 0f) { return; }

            Rect logical = sprite.rect;
            Rect dest = rect;
            if (logical.width > 0.5f && logical.height > 0.5f)
            {
                float sx = rect.width / logical.width;
                float sy = rect.height / logical.height;
                Vector2 offset = sprite.textureRectOffset;
                dest = new Rect(
                    rect.x + offset.x * sx,
                    rect.yMax - (offset.y + tr.height) * sy,   // IMGUI は上が 0 なので下端から測り直す
                    tr.width * sx,
                    tr.height * sy);
            }

            Rect uv = new Rect(tr.x / tw, tr.y / th, tr.width / tw, tr.height / th);
            Color previous = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(dest, sprite.texture, uv, true);
            GUI.color = previous;
        }

        private static void EnsureResources()
        {
            if (_panelTexture == null)
            {
                _panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _panelTexture.SetPixel(0, 0, Color.white);
                _panelTexture.Apply();
                _panelTexture.hideFlags = HideFlags.HideAndDontSave;
            }

            int fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.019f), 11, 30);
            if (_countStyle == null)
            {
                _countStyle = new GUIStyle(GUI.skin.label);
                _countStyle.alignment = TextAnchor.MiddleLeft;
                _countStyle.fontStyle = FontStyle.Bold;
                _countStyle.wordWrap = false;
                _countStyle.clipping = TextClipping.Overflow;
                _countStyle.normal.textColor = Color.white;
                _countStyle.padding = new RectOffset(0, 0, 0, 0);
                _countStyle.margin = new RectOffset(0, 0, 0, 0);
            }
            if (_countStyle.fontSize != fontSize) { _countStyle.fontSize = fontSize; }

            int overlaySize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.015f), 9, 22);
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label);
                _overlayStyle.alignment = TextAnchor.MiddleLeft;
                _overlayStyle.fontStyle = FontStyle.Bold;
                _overlayStyle.wordWrap = false;
                _overlayStyle.clipping = TextClipping.Overflow;
                _overlayStyle.normal.textColor = Color.white;
                _overlayStyle.padding = new RectOffset(0, 0, 0, 0);
                _overlayStyle.margin = new RectOffset(0, 0, 0, 0);
            }
            if (_overlayStyle.fontSize != overlaySize) { _overlayStyle.fontSize = overlaySize; }
        }

        private static Camera CurrentCamera()
        {
            CameraManager manager = CameraManager.Instance;
            if (manager == null) { return null; }
            return manager.ActiveCamera;
        }
    }
}
