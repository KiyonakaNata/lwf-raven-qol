// 本体の在庫表示（0.32.0・Alt 長押しで使い魔の上に出る吹き出し）を暗い配色に塗り替える。
//
// 通常画面のホバーは本体の在庫表示に任せた（RavenStockView.Scan）。その代わりに、
// 本体の吹き出しをマップビューの MOD の表示と同じ色にそろえる。
//
// 吹き出しは FamInventoryPeekBubble.prefab。背景は白い9スライス（bg_shortcut_w_0）を
// クリーム色で塗ったもの、数字はこげ茶の TextMeshPro。色を2つ差し替えるだけで、
// 大きさ・位置・中身には触らない。
//
// 吹き出しは最大 160 個のプールで、Awake は1個につき1回。そこで塗る。
// F6 の読み直しではもう出来ている吹き出しに Awake が来ないので、パッチの時にも一度塗る。
// 設定を切り替えたときも塗り直すので、再起動は要らない。
//
// 注釈（[HarmonyPatch]）ではなく手で当てる。PatchAll に乗せると、本体の更新でここが
// 外れたときに MOD 全体が止まる。配色は外れても他の機能に関係ないので、単独で失敗させる。

using System;
using System.Reflection;
using HarmonyLib;
using Map.InventoryPeek;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace LwfRavenQol
{
    internal static class StockBubble
    {
        // 背景は RavenStockView の札と同じ色で、透けだけ減らす（地面の模様が小さい数字に重なるため）
        private static readonly Color PanelColor = new Color(0.06f, 0.05f, 0.08f, 0.94f);
        private static readonly Color TextColor = new Color(1f, 0.97f, 0.9f, 1f);

        // 本体の色（FamInventoryPeekBubble.prefab の値）。設定を切ったときに戻す先
        private static readonly Color VanillaPanelColor = new Color(0.9607843f, 0.9176471f, 0.8627451f, 1f);
        private static readonly Color VanillaTextColor = new Color(0.14117648f, 0.11372549f, 0.08627451f, 1f);

        private const float Sharpness = 0.6f;
        private const float FaceDilate = 0.12f;
        private static Material _sharp;

        // 吹き出しだけを置く層。本体が使っていない番号（TagManager で名前なし）
        private const int BubbleLayer = 31;
        private static Camera _overlay;
        private static Camera _base;
        private static float _nextSetup;
        private static bool _setupWarned;
        private static bool _patched;

        private static FieldInfo _background;
        private static FieldInfo _counts;

        internal static void Patch(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(FamInventoryPeekBubble), "Awake");
            if (target == null) { throw new MissingMethodException("FamInventoryPeekBubble.Awake"); }
            _background = AccessTools.Field(typeof(FamInventoryPeekBubble), "_background");
            if (_background == null) { throw new MissingFieldException("FamInventoryPeekBubble._background"); }
            _counts = AccessTools.Field(typeof(FamInventoryPeekBubble), "_counts");
            if (_counts == null) { throw new MissingFieldException("FamInventoryPeekBubble._counts"); }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(StockBubble), "AfterAwake"));

            _patched = true;
            RepaintAll();
            RavenQolPlugin.StockDarkMode.SettingChanged += OnSettingChanged;
        }

        // -- ブルームの外に出す ------------------------------------------------
        // 本体のメインカメラにはブルーム（しきい値 0.9・強さ 2.04・黄色）が掛かっていて、
        // 暗い地に明るい字を置くと字のまわりに光がにじむ。字を暗くすれば弱まるが読みにくい。
        // URP のカメラスタックは「ポストプロセスを持つ最後のカメラ」までに効果を掛け、
        // その後ろのカメラの絵には掛けない。そこで吹き出しを専用の層へ移し、
        // ポストプロセスなしの重ねカメラ（Overlay）をメインカメラのスタックに足して描かせる。
        // 重ねカメラはメインカメラの子にして、画角だけ毎フレーム写す。

        internal static void Update()
        {
            if (!_patched) { return; }
            if (!RavenQolPlugin.StockDarkMode.Value) { Teardown(); return; }

            if (_overlay && _base)
            {
                _overlay.orthographic = _base.orthographic;
                _overlay.orthographicSize = _base.orthographicSize;
                _overlay.fieldOfView = _base.fieldOfView;
                _overlay.nearClipPlane = _base.nearClipPlane;
                _overlay.farClipPlane = _base.farClipPlane;
                return;
            }

            // シーンが替わるとメインカメラごと消える。作り直す
            float now = Time.unscaledTime;
            if (now < _nextSetup) { return; }
            _nextSetup = now + 1f;
            Teardown();
            try { Setup(); }
            catch (Exception e)
            {
                Teardown();
                if (!_setupWarned)
                {
                    _setupWarned = true;
                    RavenQolPlugin.Log.LogWarning("[stock] bubble camera disabled: " + e);
                }
            }
        }

        private static void Setup()
        {
            Camera main = Camera.main;
            if (!main) { return; }
            UniversalAdditionalCameraData mainData = main.GetUniversalAdditionalCameraData();
            if (mainData == null || mainData.renderType != CameraRenderType.Base || !mainData.renderPostProcessing) { return; }

            GameObject go = new GameObject("RavenQolBubbleCamera");
            go.transform.SetParent(main.transform, false);
            Camera overlay = go.AddComponent<Camera>();
            overlay.CopyFrom(main);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            overlay.cullingMask = 1 << BubbleLayer;
            UniversalAdditionalCameraData data = overlay.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Overlay;
            data.renderPostProcessing = false;

            mainData.cameraStack.Add(overlay);
            main.cullingMask &= ~(1 << BubbleLayer);
            _overlay = overlay;
            _base = main;
            RavenQolPlugin.Log.LogInfo("[stock] bubble camera on " + main.name);
        }

        /// <summary>重ねカメラを外して、メインカメラを元に戻す。吹き出しはメインカメラがまた描く。</summary>
        internal static void Teardown()
        {
            if (_base)
            {
                _base.cullingMask |= 1 << BubbleLayer;
                UniversalAdditionalCameraData mainData = _base.GetUniversalAdditionalCameraData();
                if (mainData != null && _overlay) { mainData.cameraStack.Remove(_overlay); }
            }
            if (_overlay) { UnityEngine.Object.Destroy(_overlay.gameObject); }
            _overlay = null;
            _base = null;
        }

        private static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++) { SetLayer(root.GetChild(i), layer); }
        }

        private static void OnSettingChanged(object sender, EventArgs e)
        {
            RepaintAll();
        }

        /// <summary>もう出来ている吹き出しを塗り直す（F6 の読み直し・設定の切り替え）。</summary>
        private static void RepaintAll()
        {
            foreach (FamInventoryPeekBubble bubble in Resources.FindObjectsOfTypeAll<FamInventoryPeekBubble>())
            {
                if (bubble && bubble.gameObject.scene.IsValid()) { AfterAwake(bubble); }
            }
        }

        private static void AfterAwake(FamInventoryPeekBubble __instance)
        {
            try
            {
                bool dark = RavenQolPlugin.StockDarkMode.Value;
                // 重ねカメラがまだ無い間は、メインカメラがこの層も描く（マスクを削るのは Setup）
                SetLayer(__instance.transform, dark ? BubbleLayer : 0);
                SpriteRenderer background = _background.GetValue(__instance) as SpriteRenderer;
                if (background) { background.color = dark ? PanelColor : VanillaPanelColor; }
                TextMeshPro counts = _counts.GetValue(__instance) as TextMeshPro;
                if (counts && !dark)
                {
                    // 本体の材質はフォントの既定の材質（MPLUS1-ExtraBold SDF Material）。
                    // 前の読み込みで差し替えた材質が残っていても、ここから引けば戻せる
                    counts.color = VanillaTextColor;
                    if (counts.font != null && counts.font.material != null) { counts.fontSharedMaterial = counts.font.material; }
                }
                else if (counts)
                {
                    counts.color = TextColor;
                    // 暗い地に明るい字は、SDF の縁のぼかしが字を細く・にじんで見せる。
                    // 縁を締めて少し太らせた材質を1つだけ作り、全部の吹き出しで共有する（バッチを割らない）
                    Material source = (counts.font != null && counts.font.material != null) ? counts.font.material : counts.fontSharedMaterial;
                    if (_sharp == null && source != null)
                    {
                        _sharp = new Material(source);
                        _sharp.name = source.name + " (Raven QoL)";
                        _sharp.SetFloat("_Sharpness", Sharpness);
                        _sharp.SetFloat("_FaceDilate", FaceDilate);
                    }
                    if (_sharp != null) { counts.fontSharedMaterial = _sharp; }
                }
            }
            catch (Exception e)
            {
                RavenQolPlugin.Log.LogWarning("[stock] skipped inventory bubble: " + e);
            }
        }
    }
}
