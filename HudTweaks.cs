// マップビュー（俯瞰）まわりの HUD 調整。レイヴンとは独立した見た目だけの手入れ。
//
// 1) マップビューの間、画面下の操作説明と選択物の説明を消す。
//    土地の購入と再投下しかできない画面で、工場用の説明が残っていても読む意味が無い。
//    ManualUIManager は枠（Frame / FrameCommon）ごと抱える根
//    （CanvasUI/BottomCenterUI/Centers/ManualUI）に付いているので、その根を
//    1枚の CanvasGroup で消す。パネルを個別に消すと枠だけ残る。
//    根の外にある部品（タグ行など）は、序列化フィールドから拾って別に消す。
//    SetActive では本体の切り替え（SetItem が選択パネルを付け替える）と
//    喧嘩するので、CanvasGroup の alpha で消す。
//    シーンを確認した限り ManualUI の根に本体の CanvasGroup は無い（RectTransform／
//    CanvasRenderer／ManualUIManager のみ）。そこに付く CanvasGroup は必ずこちらの物
//    なので、値は 覚えて戻す ではなく 1/0 の決め打ちにする。前の値を覚える作りは、
//    隠している最中に F6（読み直し）が挟まると記憶を失い、次に 0 を「元の値」として
//    覚え直して二度と戻らなくなる。
//
// 2) 「再投下」の札（_dropMomokoUI）とボタン2枚を、画面の左上に固定で縦に並べる。
//    課税通知・追加融資と、他の UI を基準にする方式はどれも動く（課税イベントで
//    課税通知書が中央へ移る等）ので、何にも追随しない固定位置に落ち着いた。
//    左上の角は、マップビューでは開発スポンサーを消してあるので空いている。
//    親を替えると「マップビューの間だけ出る」という本体の canvas 階層の都合が壊れ
//    かねないので、親はそのまま、画面座標（ワールド角）で位置だけ合わせる。
//    位置はマップビュー中に定期的に測り直す（解像度や UI の増減で動くため）。
//    併せて表示の再判定（RefreshDropMomokoView・公開メソッド）も定期的に呼ぶ。
//    本体はマスの移動と土地購入のときにしか再判定せず、入場の瞬間の判定
//    （投下アニメの準備など）が一拍間に合わないと、マップを動かすまで
//    札が出てこないため。
//
// どちらもゲームの挙動には触らない。Harmony も使わず、毎フレームの見張りだけ。

using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Items;
using Items.Tag;
using Tax;
using TMPro;
using UI.LandPurchaseUI;
using UI.ManualUI;
using UnityEngine;
using UnityEngine.UI;
using Utility.Localization;

namespace LwfRavenQol
{
    /// <summary>マップビュー中の HUD の消し込みと、再投下の札の置き直し。</summary>
    internal static class HudTweaks
    {
        // ManualUIManager が握っている、画面下の説明一式
        private static readonly string[] ManualFieldNames = new string[]
        {
            "_tmpEquipName",
            "_famPatronInfo",
            "_descInfo",
            "_manualNull",
            "_manualVacuum",
            "_manualSummonCrafter",
            "_manualSummonConveyor",
            "_manualSummonTransporter",
            "_manualSummonWorker",
            "_manualWandSkill",
            "_manualCommonAction",
            "_manualCommonMovement"
        };

        private static FieldInfo _fDropMomokoUI;
        private static bool _ready;

        private static BirdsEyeCameraManagerMono _birdsEye;
        private static ManualUIManager _manual;
        private static readonly List<GameObject> ManualTargets = new List<GameObject>();

        private static bool _wasMapView;
        private static bool _wasEnabled = true;
        private static bool _manualHidden;
        private static GameObject _movedDropUI;
        private static GameObject _wiredDropUI;

        // 開発スポンサーの一覧（CanvasUI/UpperLeftUI/NormalGameUIList/PatronsList）。
        // マップビューでは左上の場所を食うだけなので隠す。素の部品は RectTransform と
        // CanvasRenderer だけで、CanvasGroup を足しても誰とも喧嘩しない
        private static GameObject _patronsPanel;

        // マップビューのボタン。再投下の札の複製に Button を足したもの。
        // 札の下に [在庫]、その下に [採掘ポイント] と縦に積む
        //（横に並べると画面の右端からはみ出す）
        private static GameObject _stockButton;
        private static TextMeshProUGUI _stockButtonText;
        private static CanvasGroup _stockButtonGroup;
        private static GameObject _mineButton;
        private static TextMeshProUGUI _mineButtonText;
        private static CanvasGroup _mineButtonGroup;

        // [採掘ポイント] ボタンの状態。左の縦積み（ポータル・看板・電話・採掘ポイント）
        // 全部の ON/OFF を受け持つ
        private static bool _minePointsVisible = true;
        private static bool _mineInitialized;

        // 左の縦積みの根（CanvasUI/UpperLeftUI/NormalGameUIList）。
        // 積み方は本家どおり上から下のまま、積み始めだけボタン列の直下へずらす。
        // マップビューではゲーム自身がスポンサー枠を畳むので、ポータルが最上段へ来て
        // 左上固定のボタン列と同じ場所になり、描画順で必ず一覧が上に被るため
        private static GameObject _leftStack;
        private static bool _leftStackMoved;
        private static Vector3 _leftStackSavedPos;


        // 左の一覧のボタンは文字なしで、消す対象の絵を中央に並べる（ピッケル → 看板 → 電話）。
        // 「詳細」「情報」にあたる単独の語がゲームの言語表に無く（採掘情報だけ）、
        // ポータルの絵も、専用アイコンが無く自前で描いても浮いたのでやめた
        private static readonly List<Image> MineIcons = new List<Image>();
        private static bool _mineIconsResolved;

        // 名前でしか引けない絵の置き場。Resources.FindObjectsOfTypeAll は重いので
        // 結果を覚えて、見つからなかった名前も少し間を置いてからだけ引き直す
        private static readonly Dictionary<string, Sprite> SpriteByName = new Dictionary<string, Sprite>();
        private static float _nextSpriteProbe = float.NegativeInfinity;

        // ボタンの文字はゲームの言語表から借りる（配布先の言語に勝手に付いていく）
        private const string StockLabelKey = "StatsColumnStock";   // 統計画面の列「在庫」

        private static string _stockLabel;
        private static float _nextLabelFetch = float.NegativeInfinity;

        private const float DropRefreshInterval = 0.25f;
        private static float _nextDropRefresh;

        private const float LookupInterval = 1f;
        private static float _nextLookup = float.NegativeInfinity;

        internal static void Init()
        {
            _fDropMomokoUI = typeof(BirdsEyeCameraManagerMono).GetField(
                "_dropMomokoUI", BindingFlags.Instance | BindingFlags.NonPublic);
            _ready = _fDropMomokoUI != null;
            if (!_ready)
            {
                RavenQolPlugin.Log.LogWarning("[hud] _dropMomokoUI not found; redeploy relocation disabled.");
            }
        }

        /// <summary>HUD の canvas（CanvasUI）。中身の表示が UI との重なりを避けるのに使う。</summary>
        internal static Canvas MainCanvas()
        {
            if (_manual != null && (bool)_manual)
            {
                return _manual.GetComponentInParent<Canvas>();
            }
            return null;
        }

        internal static bool IsMapViewActive()
        {
            if (_birdsEye == null || !_birdsEye) { return false; }
            if (!_birdsEye.gameObject.activeInHierarchy) { return false; }
            return _birdsEye.IsOnBirdsEyeView();
        }

        internal static void Update()
        {
            if (!RavenQolPlugin.MapEnabled.Value)
            {
                // 触った跡を戻してから休む。戻すのは切り替わりの一度だけ
                if (_wasEnabled) { RestoreOnUnload(); _wasMapView = false; }
                _wasEnabled = false;
                return;
            }
            _wasEnabled = true;

            EnsureInstances();

            bool mapView = IsMapViewActive();
            if (mapView != _wasMapView)
            {
                _wasMapView = mapView;
                if (mapView)
                {
                    if (RavenQolPlugin.MapHideManual.Value) { SetManualVisible(false); }
                    SetPatronsVisible(false);
                    if (!_mineInitialized)
                    {
                        // 左の一覧（ポータル・看板・電話・採掘ポイント）は最初は出し、
                        // 出し入れは絵3枚ボタンに任せる
                        _minePointsVisible = true;
                        _mineInitialized = true;
                    }
                    SetLeftStackVisible(_minePointsVisible);
                    _nextDropRefresh = float.NegativeInfinity;   // 入場の直後に1回目を回す
                }
                else
                {
                    SetManualVisible(true);
                    SetPatronsVisible(true);
                    RestoreLeftStackPlacement();
                    SetLeftStackVisible(true);
                    HideMapButtons();
                }
            }

            if (!mapView)
            {
                // 出入りの特定の順序（ランをまたぐ等）で残ることがあるので、
                // 切り替わりの一度きりではなく、毎フレーム押さえ込む
                HideMapButtons();
            }

            if (mapView)
            {
                float now = Time.unscaledTime;
                if (now >= _nextDropRefresh)
                {
                    _nextDropRefresh = now + DropRefreshInterval;
                    MoveDropUI();
                    if (_birdsEye != null && (bool)_birdsEye) { _birdsEye.RefreshDropMomokoView(); }
                    UpdateMapButtons();
                }
            }
        }

        // ---- 実体の掴み直し（シーンの作り直しで消えるため） --------------------

        private static void EnsureInstances()
        {
            if (_birdsEye != null && (bool)_birdsEye && _manual != null && (bool)_manual) { return; }

            float now = Time.unscaledTime;
            if (now < _nextLookup) { return; }
            _nextLookup = now + LookupInterval;

            if (_birdsEye == null || !_birdsEye)
            {
                _birdsEye = UnityEngine.Object.FindAnyObjectByType<BirdsEyeCameraManagerMono>(FindObjectsInactive.Include);
                _movedDropUI = null;
            }
            if (_manual == null || !_manual)
            {
                // 逆コンパイルのシーンでは同じスクリプトが別の物にも付いて見える。
                // 序列化フィールドが埋まっている個体だけを本物と見なす
                ManualUIManager found = null;
                ManualUIManager[] candidates = UnityEngine.Object.FindObjectsByType<ManualUIManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                FieldInfo probe = typeof(ManualUIManager).GetField(
                    "_manualCommonAction", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (probe == null) { found = candidates[i]; break; }
                    Component value = probe.GetValue(candidates[i]) as Component;
                    if (value != null && value) { found = candidates[i]; break; }
                }
                if (found != _manual)
                {
                    _manual = found;
                    _manualHidden = false;
                    CollectManualTargets();
                    // 読み直しの前から alpha=0 が残っていることがある。見つけた時点で正常に戻す
                    if (!IsMapViewActive()) { SetManualVisible(true); }
                }
            }
        }

        private static void CollectManualTargets()
        {
            ManualTargets.Clear();
            if (_manual == null) { return; }

            // 枠ごと抱える根を丸ごと。個々のパネルだと枠（Frame / FrameCommon）が残る
            Transform root = _manual.transform;
            ManualTargets.Add(root.gameObject);

            // 根の外にぶら下がっている部品だけ個別に足す
            BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            for (int i = 0; i < ManualFieldNames.Length; i++)
            {
                FieldInfo field = typeof(ManualUIManager).GetField(ManualFieldNames[i], f);
                if (field == null) { continue; }
                Component component = field.GetValue(_manual) as Component;
                if (component == null || !component) { continue; }
                if (component.transform.IsChildOf(root)) { continue; }
                if (!ManualTargets.Contains(component.gameObject)) { ManualTargets.Add(component.gameObject); }
            }
        }

        // ---- 操作説明の消し込み ----------------------------------------------

        private static void SetManualVisible(bool visible)
        {
            _manualHidden = !visible;
            for (int i = 0; i < ManualTargets.Count; i++)
            {
                GameObject target = ManualTargets[i];
                if (target == null || !target) { continue; }
                CanvasGroup group = target.GetComponent<CanvasGroup>();
                if (group == null)
                {
                    if (visible) { continue; }   // 見せる側なら、無いものはそのままでいい
                    group = target.AddComponent<CanvasGroup>();
                }
                group.alpha = (visible ? 1f : 0f);
                group.interactable = visible;
                group.blocksRaycasts = visible;
            }
        }

        /// <summary>
        /// 開発スポンサーの一覧の出し入れ。alpha で消して**場所は残す**。
        /// SetActive で空間ごと詰めると、ポータルが最上段へ上がってきて、左上に固定した
        /// ボタン列の上に被さる（左の一覧はこちらの札より後に描かれるため、必ず向こうが勝つ）。
        /// 空けたスポンサーの区画を、そのままボタン列の置き場にする。
        /// </summary>
        private static void SetPatronsVisible(bool visible)
        {
            if (_patronsPanel == null || !_patronsPanel)
            {
                Canvas canvas = MainCanvas();
                if (canvas == null) { return; }
                Transform found = FindChildRecursive(canvas.transform, "PatronsList");
                if (found == null) { return; }
                _patronsPanel = found.gameObject;
            }
            CanvasGroup group = _patronsPanel.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (visible) { return; }
                group = _patronsPanel.AddComponent<CanvasGroup>();
            }
            group.alpha = (visible ? 1f : 0f);
            group.interactable = visible;
            group.blocksRaycasts = visible;
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) { return null; }
            if (root.name == name) { return root; }
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) { return found; }
            }
            return null;
        }

        /// <summary>F6 の読み直しで置き去りにしないための後始末。</summary>
        internal static void RestoreOnUnload()
        {
            SetManualVisible(true);
            SetPatronsVisible(true);
            RestoreLeftStackPlacement();
            SetLeftStackVisible(true);
            if (_stockButton != null && (bool)_stockButton) { UnityEngine.Object.Destroy(_stockButton); }
            if (_mineButton != null && (bool)_mineButton) { UnityEngine.Object.Destroy(_mineButton); }
            _stockButton = null;
            _mineButton = null;
        }

        // ---- 「在庫」ボタン（再投下の札の複製） ------------------------------

        /// <summary>
        /// マップビュー中、再投下の札の下に [採掘ポイント][在庫] のボタンを添える。
        /// 目立たせたいので、見た目は札そのものの複製（同じ書式が一番揃う）。
        /// 元の札は本体が出し入れするが、複製はマップビューの間ずっと出しておく。
        /// </summary>
        private static void UpdateMapButtons()
        {
            if (!_ready || _birdsEye == null || !_birdsEye) { return; }
            GameObject dropUI = _fDropMomokoUI.GetValue(_birdsEye) as GameObject;
            if (dropUI == null || !dropUI) { return; }
            RectTransform dropRect = dropUI.transform as RectTransform;
            if (dropRect == null) { return; }

            RefreshLabels();
            if (_stockButton == null || !_stockButton)
            {
                _stockButton = CloneTagButton(dropRect, "RavenQolStockButton", "Summon-TransporterExit",
                    OnStockButtonClicked, out _stockButtonText, out _stockButtonGroup);
            }
            if (_mineButton == null || !_mineButton)
            {
                _mineButton = CloneTagButton(dropRect, "RavenQolMineButton", null,
                    OnMineButtonClicked, out _mineButtonText, out _mineButtonGroup);
                SetupMineButtonIcons(_mineButton);
            }
            if (_stockButton == null || _mineButton == null) { return; }
            _stockButton.SetActive(true);
            _mineButton.SetActive(true);

            // 再投下の札の下に左端を揃えて縦積み（札が隠れていても矩形は生きている）
            RectTransform stockRect = _stockButton.transform as RectTransform;
            RectTransform mineRect = _mineButton.transform as RectTransform;
            Vector3[] drop = new Vector3[4];
            dropRect.GetWorldCorners(drop);
            float gap = (drop[1].y - drop[0].y) * 0.15f;

            Vector3[] corners = new Vector3[4];
            stockRect.GetWorldCorners(corners);
            stockRect.position += new Vector3(drop[0].x - corners[0].x, (drop[0].y - gap) - corners[1].y, 0f);
            stockRect.GetWorldCorners(corners);   // 動かした後の位置から真下を測る
            Vector3[] mine = new Vector3[4];
            mineRect.GetWorldCorners(mine);
            mineRect.position += new Vector3(corners[0].x - mine[0].x, (corners[0].y - gap) - mine[1].y, 0f);

            if (_stockButtonText != null && _stockButtonText.text != _stockLabel) { _stockButtonText.SetText(_stockLabel); }
            if (_mineButtonText != null && _mineButtonText.text.Length != 0) { _mineButtonText.SetText(""); }
            if (!_mineIconsResolved) { ApplyMineIcons(); }
            CenterMineIcons();
            ApplyButtonStates();

            // 左の一覧（ポータル…）の積み始めを、ボタン列の下端の少し下へ。
            // 積みの先頭には透明にしたスポンサー枠が居座っているので、その高さぶん
            // 引き上げて、見える先頭（ポータル）がボタンの直下に来るようにする。
            // 透明枠はボタン列の裏に重なるが、見えず、クリックも素通しなので実害はない
            mineRect.GetWorldCorners(mine);
            PlaceLeftStackBelow(mine[0].y - gap * 2f + PatronsSpaceHeight());
        }

        /// <summary>透明にしたスポンサー枠が積みの中で食っている高さ（ワールド座標）。</summary>
        private static float PatronsSpaceHeight()
        {
            if (_patronsPanel == null || !_patronsPanel) { return 0f; }
            if (!_patronsPanel.activeInHierarchy) { return 0f; }   // 畳まれていれば場所も無い
            RectTransform rect = _patronsPanel.transform as RectTransform;
            if (rect == null) { return 0f; }
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Mathf.Max(0f, corners[1].y - corners[0].y);
        }

        /// <summary>左の縦積みの上端を指定の高さへ。積み方そのものはレイアウト任せのまま。</summary>
        private static void PlaceLeftStackBelow(float worldTopY)
        {
            if (!EnsureLeftStack()) { return; }
            RectTransform rect = _leftStack.transform as RectTransform;
            if (rect == null) { return; }

            LayoutElement element = _leftStack.GetComponent<LayoutElement>();
            if (element == null) { element = _leftStack.AddComponent<LayoutElement>(); }
            if (!_leftStackMoved)
            {
                _leftStackSavedPos = rect.anchoredPosition3D;
                _leftStackMoved = true;
            }
            element.ignoreLayout = true;

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            rect.position += new Vector3(0f, worldTopY - corners[1].y, 0f);
        }

        private static void RestoreLeftStackPlacement()
        {
            if (!_leftStackMoved) { return; }
            _leftStackMoved = false;
            if (_leftStack == null || !_leftStack) { return; }
            RectTransform rect = _leftStack.transform as RectTransform;
            LayoutElement element = _leftStack.GetComponent<LayoutElement>();
            if (element != null) { element.ignoreLayout = false; }
            if (rect != null)
            {
                rect.anchoredPosition3D = _leftStackSavedPos;
                RectTransform parent = rect.parent as RectTransform;
                if (parent != null) { LayoutRebuilder.MarkLayoutForRebuild(parent); }
            }
        }

        /// <summary>再投下の札を複製して、押せる札に仕立てる。</summary>
        private static GameObject CloneTagButton(RectTransform dropRect, string name, string iconItemID,
            UnityEngine.Events.UnityAction onClick, out TextMeshProUGUI text, out CanvasGroup group)
        {
            text = null;
            group = null;

            // F6 の読み直しで前の複製が取り残されていたら、先に片付ける
            Transform stale = dropRect.parent.Find(name);
            while (stale != null)
            {
                UnityEngine.Object.DestroyImmediate(stale.gameObject);
                stale = dropRect.parent.Find(name);
            }

            GameObject clone = UnityEngine.Object.Instantiate(dropRect.gameObject, dropRect.parent);
            clone.name = name;

            // 複製の中のローカライズ部品が生きていると、文字を勝手に書き戻される
            // （本体の TaxUIUtility は internal なので、同じことを直にやる）
            UnityEngine.Localization.Components.LocalizeStringEvent[] localizeEvents =
                clone.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true);
            for (int i2 = 0; i2 < localizeEvents.Length; i2++) { localizeEvents[i2].enabled = false; }
            text = clone.GetComponentInChildren<TextMeshProUGUI>(true);

            // 本物の札にはカーソルが近づくと薄くする係（CursorProximityCanvasGroupFader）が
            // 付いていて、こちらの ON/OFF の透明度と喧嘩する。複製からは外す
            Component[] components = clone.GetComponents<Component>();
            for (int i2 = 0; i2 < components.Length; i2++)
            {
                if (components[i2] != null && components[i2].GetType().Name == "CursorProximityCanvasGroupFader")
                {
                    UnityEngine.Object.Destroy(components[i2]);
                }
            }

            // 絵は再投下のものが入っているので、指定のアイテムに差し替える
            // （null のときは呼び元が自分で仕込む）
            Transform iconChild = clone.transform.Find("icon");
            if (iconChild != null && iconItemID != null)
            {
                Image iconImage = iconChild.GetComponent<Image>();
                if (iconImage != null)
                {
                    iconImage.sprite = ItemParamGetter.GetSprite(iconItemID);
                    iconImage.preserveAspect = true;
                }
            }

            Button button = clone.GetComponent<Button>();
            if (button == null) { button = clone.AddComponent<Button>(); }
            Image graphic = clone.GetComponentInChildren<Image>(true);
            if (graphic != null) { button.targetGraphic = graphic; }
            button.onClick.AddListener(onClick);

            // 本物は表示専用でクリックを素通しする設定（Interactable/BlocksRaycasts = 0）。
            // ボタンにするので通す側に倒す
            group = clone.GetComponent<CanvasGroup>();
            if (group == null) { group = clone.AddComponent<CanvasGroup>(); }
            group.interactable = true;
            group.blocksRaycasts = true;

            clone.SetActive(true);
            RavenQolPlugin.Log.LogInfo("[hud] built " + name + " from the redeploy tag");
            return clone;
        }

        /// <summary>
        /// [採掘ポイント] ボタンの icon 子を4枚に増やして、消す対象の絵を並べる。
        /// 絵そのものはまだ読み込まれていないことがあるので、張るのは ApplyMineIcons で。
        /// </summary>
        private static void SetupMineButtonIcons(GameObject button)
        {
            MineIcons.Clear();
            _mineIconsResolved = false;
            if (button == null) { return; }
            Transform iconChild = button.transform.Find("icon");
            if (iconChild == null) { return; }
            Image first = iconChild.GetComponent<Image>();
            if (first == null) { return; }
            RectTransform firstRect = iconChild as RectTransform;
            float step = firstRect.rect.width * 1.15f;

            MineIcons.Add(first);
            for (int i = 1; i < 3; i++)
            {
                GameObject copy = UnityEngine.Object.Instantiate(iconChild.gameObject, iconChild.parent);
                copy.name = "icon" + i;
                RectTransform copyRect = copy.transform as RectTransform;
                copyRect.anchoredPosition = firstRect.anchoredPosition + new Vector2(step * i, 0f);
                MineIcons.Add(copy.GetComponent<Image>());
            }
            ApplyMineIcons();
        }

        /// <summary>
        /// 3枚の絵をボタンの中央へ寄せる。等間隔のまま、群れの中心をボタンの中心に合わせる。
        /// 毎回同じ目標へ合わせ直すだけなので、既に合っていれば何も動かない。
        /// </summary>
        private static void CenterMineIcons()
        {
            if (MineIcons.Count < 3 || _mineButton == null || !_mineButton) { return; }
            RectTransform button = _mineButton.transform as RectTransform;
            if (button == null) { return; }

            Vector3[] corners = new Vector3[4];
            button.GetWorldCorners(corners);
            float buttonCenter = (corners[0].x + corners[3].x) * 0.5f;

            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < 3; i++)
            {
                Image image = MineIcons[i];
                if (image == null || !image) { return; }
                RectTransform rect = image.rectTransform;
                rect.GetWorldCorners(corners);
                minX = Mathf.Min(minX, corners[0].x);
                maxX = Mathf.Max(maxX, corners[3].x);
            }
            float delta = buttonCenter - (minX + maxX) * 0.5f;
            if (Mathf.Abs(delta) < 0.0001f) { return; }
            for (int i = 0; i < 3; i++)
            {
                MineIcons[i].rectTransform.position += new Vector3(delta, 0f, 0f);
            }
        }

        /// <summary>3枚の絵を張る。読み込み待ちの絵があれば、揃うまで定期処理から呼ばれ続ける。</summary>
        private static void ApplyMineIcons()
        {
            if (MineIcons.Count < 3) { _mineIconsResolved = true; return; }
            bool allResolved = true;
            for (int i = 0; i < 3; i++)
            {
                Image image = MineIcons[i];
                if (image == null || !image) { continue; }
                Sprite sprite = ResolveMineIcon(i);
                if (sprite == null) { allResolved = false; continue; }
                if (image.sprite != sprite)
                {
                    image.sprite = sprite;
                    image.preserveAspect = true;
                }
            }
            _mineIconsResolved = allResolved;
        }

        private static Sprite ResolveMineIcon(int index)
        {
            switch (index)
            {
                case 0: return SafeItemSprite("Pickaxe");            // 採掘ポイント
                case 1: return FindSpriteByName("icon_Signboard");   // 看板
                case 2: return SafeTagSprite("FasterOrder");         // 電話（注文レリックの絵）
            }
            return null;
        }

        private static Sprite SafeTagSprite(string tagID)
        {
            try { return TagParamGetter.GetSprite(tagID); }
            catch (System.Exception) { return null; }
        }

        private static Sprite SafeItemSprite(string itemID)
        {
            try { return ItemParamGetter.GetSprite(itemID); }
            catch (System.Exception) { return null; }
        }

        /// <summary>
        /// 読み込まれている中から名前で絵を探す。全走査で重いので、結果は覚えて
        /// 見つからなかったときだけ3秒あけて引き直す（economy_graph と同じ手）。
        /// </summary>
        private static Sprite FindSpriteByName(string name)
        {
            Sprite cached;
            if (SpriteByName.TryGetValue(name, out cached) && cached != null) { return cached; }
            float now = Time.unscaledTime;
            if (now < _nextSpriteProbe) { return null; }
            _nextSpriteProbe = now + 3f;

            Sprite[] all = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name)
                {
                    SpriteByName[name] = all[i];
                    break;
                }
            }
            if (SpriteByName.TryGetValue(name, out cached)) { return cached; }
            return null;
        }

        private static void OnStockButtonClicked()
        {
            StockView.MapOverlayOn = !StockView.MapOverlayOn;
            ApplyButtonStates();
        }

        private static void OnMineButtonClicked()
        {
            _minePointsVisible = !_minePointsVisible;
            SetLeftStackVisible(_minePointsVisible);
            ApplyButtonStates();
            RavenQolPlugin.Log.LogInfo("[hud] left panels " + (_minePointsVisible ? "shown" : "hidden"));
        }

        /// <summary>OFF のときは薄くして、押せる札と分かる程度に残す。</summary>
        private static void ApplyButtonStates()
        {
            if (_stockButtonGroup != null && (bool)_stockButtonGroup)
            {
                _stockButtonGroup.alpha = (StockView.MapOverlayOn ? 1f : 0.55f);
                _stockButtonGroup.interactable = true;
                _stockButtonGroup.blocksRaycasts = true;
            }
            if (_mineButtonGroup != null && (bool)_mineButtonGroup)
            {
                _mineButtonGroup.alpha = (_minePointsVisible ? 1f : 0.55f);
                _mineButtonGroup.interactable = true;
                _mineButtonGroup.blocksRaycasts = true;
            }
        }

        private static void HideMapButtons()
        {
            if (_stockButton != null && (bool)_stockButton && _stockButton.activeSelf) { _stockButton.SetActive(false); }
            if (_mineButton != null && (bool)_mineButton && _mineButton.activeSelf) { _mineButton.SetActive(false); }
        }

        private static bool EnsureLeftStack()
        {
            if (_leftStack != null && (bool)_leftStack) { return true; }
            Canvas canvas = MainCanvas();
            if (canvas == null) { return false; }
            Transform found = FindChildRecursive(canvas.transform, "NormalGameUIList");
            if (found == null) { return false; }
            _leftStack = found.gameObject;
            return true;
        }

        /// <summary>
        /// 左の縦積み（ポータル・看板・電話・採掘ポイント）全部の出し入れ。
        /// 根に1枚の CanvasGroup を掛けるだけで、本体が子をどう出し入れしているかには
        /// 一切触らない。active を横から触る方式は、本体が消していた物を点け直したり、
        /// こちらが消した状態のまま固定したりの事故が起きた（ポータルが消えたまま等）。
        /// </summary>
        private static void SetLeftStackVisible(bool visible)
        {
            if (!EnsureLeftStack()) { return; }
            CanvasGroup group = _leftStack.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (visible) { return; }
                group = _leftStack.AddComponent<CanvasGroup>();
            }
            group.alpha = (visible ? 1f : 0f);
            group.interactable = visible;
            group.blocksRaycasts = visible;
        }

        /// <summary>ゲームの言語表から札の文字。言語切り替えに付いていけるよう、少し間を置いて引き直す。</summary>
        private static void RefreshLabels()
        {
            float now = Time.unscaledTime;
            if (_stockLabel != null && now < _nextLabelFetch) { return; }
            _nextLabelFetch = now + 5f;
            _stockLabel = FetchMessage(StockLabelKey, "Stock");
        }

        private static string FetchMessage(string key, string fallback)
        {
            string text = null;
            try { text = LocalizedTextGetter.GetMessageData(key); }
            catch (System.Exception) { }
            return (string.IsNullOrEmpty(text) ? fallback : text);
        }

        // ---- 再投下の札の置き直し --------------------------------------------

        private static void MoveDropUI()
        {
            if (!_ready || _birdsEye == null || !_birdsEye) { return; }

            GameObject dropUI = _fDropMomokoUI.GetValue(_birdsEye) as GameObject;
            if (dropUI == null || !dropUI) { return; }
            RectTransform dropRect = dropUI.transform as RectTransform;
            if (dropRect == null) { return; }

            Canvas canvas = MainCanvas();
            if (canvas == null) { return; }
            RectTransform canvasRect = canvas.transform as RectTransform;
            Vector3[] cv = new Vector3[4];
            canvasRect.GetWorldCorners(cv);
            float width = cv[2].x - cv[0].x;
            float height = cv[1].y - cv[0].y;

            // 画面の左上に固定。左の余白1%・上の余白2%
            float targetLeft = cv[0].x + width * 0.01f;
            float targetTop = cv[1].y - height * 0.02f;

            Vector3[] drop = new Vector3[4];
            dropRect.GetWorldCorners(drop);
            dropRect.position += new Vector3(targetLeft - drop[0].x, targetTop - drop[1].y, 0f);

            if (_movedDropUI != dropUI)
            {
                _movedDropUI = dropUI;
                RavenQolPlugin.Log.LogInfo("[hud] pinned the redeploy tag to the top left");
            }

            WireRedeployClick(dropUI);
        }

        /// <summary>
        /// 再投下の札をクリックでも発動できるようにする。本体の札はキーの案内
        /// （クリック素通し）だが、下に押せるボタンを並べた以上、これも押せないと不自然。
        /// 発動はキー入力と同じ入口（DropMomoko・自前で状態を検査する）を呼ぶだけ。
        /// </summary>
        private static void WireRedeployClick(GameObject dropUI)
        {
            if (_wiredDropUI == dropUI) { return; }
            _wiredDropUI = dropUI;

            Button button = dropUI.GetComponent<Button>();
            if (button == null) { button = dropUI.AddComponent<Button>(); }
            Image graphic = dropUI.GetComponentInChildren<Image>(true);
            if (graphic != null) { button.targetGraphic = graphic; }
            // F6 の読み直しで、死んだ静的メソッドを掴んだ古い口が残らないように
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(OnRedeployClicked);

            CanvasGroup group = dropUI.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            // カーソルが近づくと札を薄くする係は、ボタンにした以上むしろ邪魔（押そうと
            // 近づくたび消えかける）。切っておく
            Component[] components = dropUI.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null && components[i].GetType().Name == "CursorProximityCanvasGroupFader")
                {
                    Behaviour behaviour = components[i] as Behaviour;
                    if (behaviour != null) { behaviour.enabled = false; }
                }
            }
            RavenQolPlugin.Log.LogInfo("[hud] made the redeploy tag clickable");
        }

        private static void OnRedeployClicked()
        {
            if (_birdsEye == null || !_birdsEye) { return; }
            GameObject dropUI = _fDropMomokoUI.GetValue(_birdsEye) as GameObject;
            if (dropUI == null || !dropUI || !dropUI.activeSelf) { return; }   // 札が出ていない＝発動の条件を満たしていない
            try
            {
                _birdsEye.DropMomoko(_birdsEye.GetCancellationTokenOnDestroy()).Forget();
            }
            catch (System.Exception e)
            {
                RavenQolPlugin.Log.LogError("[hud] redeploy failed: " + e);
            }
        }
    }
}
