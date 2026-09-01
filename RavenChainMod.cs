// LWF Raven QoL
//
// Lazy Witch's Factory（ver 0.24.1）に、運送レイヴンの「中継線をドラッグで一気に敷く」
// 操作を足す BepInEx Mod。
//
// -- 何をするか ---------------------------------------------------------
// レイヴンを1体置いて「荷下ろし地点設定」になったら、そのまま L-Click で引く。すると、
//
//   [レイヴン] ──range── [荷下ろし地点][レイヴン] ──range── [荷下ろし地点][レイヴン] …
//
// を、運搬範囲いっぱいの間隔で並べていく。荷下ろし地点は次のレイヴンの真隣に置き、
// 向きを揃えるので、荷下ろし地点の出力口が次のレイヴンの入力口に噛み合う。
//
// 間隔は置くたびに CarryingRange を引き直すので、契約で範囲が伸び縮みしても常に最大。
// 範囲はマンハッタン距離のひし形なので、斜めに引けば斜めの角へ落ちる（ReachPoint）。
//
// 離したときは、カーソルの下を見て終端を決める。
//   ・繋げる荷下ろし地点  → 新しく置かず、そこを行き先として選ぶ
//   ・受け口を持つもの    → その入力口の隣へ置いて噛み合わせる
//   ・それ以外            → 離した位置（範囲内に丸める）へ置く
//
// -- なぜ本体の口だけで足りるか -----------------------------------------
// 本体には既に「押しっぱなしの間、毎フレーム PerformHoldAction を呼ぶ」枠がある
// （ClickActionManager.Update → EquipActionHandler.OnHoldAction）。コンベアの連続
// 敷設はこの枠で書かれていて、レイヴン側は **null を返すだけで空いている**。
// そこへ割り込む。置くこと自体は本体の召喚経路
//   SummonStrategyDistributor.CallStrategyWithCheckAsync
// をそのまま呼ぶので、土地の所有判定・占有判定・接続配線・音・セーブは本体任せ。
// 狙う座標は CursorHitChecker.SetAdjustedChildGridGlobalAddress で差し替える
// （コンベアのドラッグが使っているのと同じ道具）。
//
// -- 向きの理屈 ---------------------------------------------------------
// 荷下ろし地点は IOPattern.BackOut ＝ 背面1方向だけ出力。
// レイヴンは IOPattern.MainIn   ＝ 正面1方向だけ入力。
// 東へ伸ばすなら、荷下ろし地点も次のレイヴンも「西向き」で揃う。
//   荷下ろし地点(西向き) の背面 = 東 → 出力が東隣へ
//   レイヴン(西向き)     の正面 = 西 → 入力が西隣（＝その荷下ろし地点）から
//
// -- 制約 ---------------------------------------------------------------
// ・レイヴン1体につき触媒を1個使う。在庫が切れたらそこで止まる。
// ・未購入の土地・埋まっているマスには置けない。ぶつかったらそこで止まる。
// ・運搬範囲はマンハッタン距離のひし形（GridSizeMeasure.CreateGridBoundOffsets）。
//   1区間の長さは floor(CarryingRange)。
//
// -- 書き方の制約 -------------------------------------------------------
// C# 5 コンパイラ（csc.exe / .NET Framework 4.0）でビルドするため、
// 文字列補間・?. 演算子・式形式メンバ・out var は使えない。
// さらに **async UniTask<T> が書けない**（カスタム AsyncMethodBuilder は C# 7 以降）。
// 置く処理は Addressables の生成を挟むので本当に非同期になりうる。そこで
// UniTaskExtensions.ContinueWith でメソッドを数珠繋ぎにして状態機械を手で書いている。
// ContinueWith には Func<T,TR> と Func<T,UniTask<TR>> の両方があり、C# 5 は
// ラムダでもメソッドグループでも戻り値で選び分けられない（改善は C# 7.3 から）。
// 繋ぐ先は **型を書いたデリゲート変数に一度受けてから** 渡すこと。

using System;
using System.Collections.Generic;
using System.Reflection;
using BaseSystem;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using Fams.EditIfFamAdd;
using GameState;
using Fams.ParamModification;
using Fams.Summon.Strategy;
using HarmonyLib;
using Input.PlayerAction;
using Items.Equip;
using Items.Equip.EditIfEquipsAdd.Equips;
using Items.Equip.EditIfEquipsAdd.Equips.SummonCatalysts;
using Items.Inventory;
using Map;
using Map.Directions;
using Map.MapObjects.IOPort;
using Map.MapObjects.PureClass;
using Map.MapObjects.PureClass.FamObjects;
using Map.PlacerHelpers;
using UI.Cursor;
using UI.Cursor.Preview;
using UI.SelectableWindow.Managers;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LwfRavenQol
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RavenQolPlugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "kiyonakanata.lwfravenqol";
        internal const string PluginName = "LWF Raven QoL";
        internal const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        private Harmony _harmony;
        internal static ConfigEntry<bool> ChainEnabled;
        internal static ConfigEntry<bool> RetargetEnabled;
        internal static ConfigEntry<bool> MapEnabled;
        internal static ConfigEntry<bool> KeepOldExit;
        internal static ConfigEntry<bool> MapHideManual;
        internal static ConfigEntry<bool> StockVisible;
        internal static ConfigEntry<bool> StockMapOverlay;

        private void Awake()
        {
            Log = Logger;

            ChainEnabled = Config.Bind(
                "1. Relay Chain", "Enabled", true, "");

            RetargetEnabled = Config.Bind(
                "2. Retarget", "Enabled", true, "");

            // 本体は繋ぐレイヴンが居なくなった荷下ろし地点を自動で片付ける（ReleaseIfNoLinkedTransporter）。
            // 配送先を選び直しただけで消えると、中の荷ごと失われるし、Ctrl で他のレイヴンを
            // 後から繋ぐ使い方もできなくなる。Tab で入った変更のときだけ片付けを止める。
            KeepOldExit = Config.Bind(
                "2. Retarget", "Keep the old dispatch port", true, "");

            StockVisible = Config.Bind(
                "3. Stock Display", "Enabled", true, "");

            // マップビューの「在庫」ボタンの初期状態。普段は隠して、見たいときだけボタンで出す
            StockMapOverlay = Config.Bind(
                "3. Stock Display", "Show map-view stock on entry", false, "");

            MapEnabled = Config.Bind(
                "4. Map View", "Enabled", true, "");

            MapHideManual = Config.Bind(
                "4. Map View", "Hide control hints", true, "");

            Chain.Init();
            Trip.Init();
            HudTweaks.Init();

            _harmony = new Harmony(PluginGuid);
            // PatchAll(Type) は「渡した型そのもの」しか見ない。入れ子にした注釈クラスは
            // 拾われず、黙って0箇所パッチになる。アセンブリごと渡すこと。
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            int patched = 0;
            foreach (MethodBase m in _harmony.GetPatchedMethods())
            {
                patched++;
                Log.LogInfo("[boot] patch: " + m.DeclaringType.Name + "." + m.Name);
            }

            Log.LogInfo("[boot] " + PluginName + " " + PluginVersion
                + "  retarget=" + (RetargetEnabled.Value ? "on" : "off")
                + "  patches=" + patched);
            if (patched == 0)
            {
                Log.LogError("[boot] no patches applied; the mod will do nothing.");
            }
        }

        private void Update()
        {
            try { Chain.UpdateRetarget(); }
            catch (Exception e) { Log.LogError("[retarget] " + e); }

            try { StockView.Update(); }
            catch (Exception e) { Log.LogError("[stock] " + e); }

            try { HudTweaks.Update(); }
            catch (Exception e) { Log.LogError("[hud] " + e); }
        }

        private void OnGUI()
        {
            try { StockView.Draw(); }
            catch (Exception e) { Log.LogError("[stock] " + e); }
        }

        /// <summary>
        /// ScriptEngine（F6 で読み直す開発用）で入れ替えたとき、古い割り込みを必ず外す。
        /// 残っていると 1 回のドラッグで中継線が二重に生えて触媒が倍溶ける。
        /// </summary>
        private void OnDestroy()
        {
            try { HudTweaks.RestoreOnUnload(); }
            catch (Exception e) { Log.LogWarning("[hud] " + e); }

            if (_harmony == null) { return; }
            try { _harmony.UnpatchSelf(); }
            catch (Exception e) { Log.LogWarning("[unpatch] " + e); }
            _harmony = null;
        }
    }

    /// <summary>
    /// 本体への割り込み。5点だけ。
    ///   ・レイヴン／荷下ろし地点の触媒インスタンスを掴む（生成の瞬間・2点）
    ///   ・押し込みを飲み込む（PerformAction。即座に置かれるとドラッグが始まらない）
    ///   ・押しっぱなしの空き枠（PerformHoldAction）に中継線の1区間を差し込む
    ///   ・ボタンを離した合図を拾う（ドラッグの終わり）
    /// </summary>
    internal static class Patches
    {
        [HarmonyPatch(typeof(TransporterCatalystEquip), MethodType.Constructor,
            new Type[] { typeof(InventoryManager), typeof(FamType), typeof(GridManager), typeof(InventoryUIManager) })]
        internal static class RavenEquipCtor
        {
            private static void Postfix(TransporterCatalystEquip __instance)
            {
                Chain.SetRavenEquip(__instance);
            }
        }

        [HarmonyPatch(typeof(TransporterExitCatalystEquip), MethodType.Constructor,
            new Type[] { typeof(InventoryManager), typeof(FamType), typeof(GridManager), typeof(InventoryUIManager) })]
        internal static class ExitEquipCtor
        {
            private static void Postfix(TransporterExitCatalystEquip __instance)
            {
                Chain.SetExitEquip(__instance);
            }
        }

        // 荷下ろし地点設定モード中の「押しっぱなし」。本体は null を返すだけなので、
        // ここを丸ごと差し替えても失うものが無い。
        [HarmonyPatch(typeof(TransporterExitCatalystEquip), "PerformHoldAction", new Type[] { typeof(float) })]
        internal static class ExitHold
        {
            private static bool Prefix(TransporterExitCatalystEquip __instance, float holdCounter,
                ref UniTask<MapObject> __result)
            {
                try
                {
                    Chain.TrackHold(holdCounter);
                    if (!Chain.ShouldChain()) { return true; }
                    __result = Chain.Step(__instance);
                    return false;
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[hold] " + e);
                    return true;
                }
            }
        }

        // 「荷下ろし地点設定」中に修飾キーを押しながら押し込んだ L-Click。
        // 本体はここで即座に荷下ろし地点を置いてモードを閉じてしまう。それだと
        // 「置く → 修飾キーを押しながら引く」の2段構えが成り立たないので、
        // 押し込みだけ飲み込んで、続きはドラッグ（PerformHoldAction）に任せる。
        [HarmonyPatch(typeof(TransporterExitCatalystEquip), "PerformAction")]
        internal static class ExitClick
        {
            private static bool Prefix(ref UniTask<MapObject> __result)
            {
                try
                {
                    if (!Chain.TrySwallowClick()) { return true; }
                    __result = UniTask.FromResult<MapObject>(null);
                    return false;
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[click] " + e);
                    return true;
                }
            }
        }

        // Tab（本体の「モード切替」）に相乗りする。レイヴンにカーソルが乗っていて、
        // かつ設定モードに入っていないときだけ横取りして配送先の変更に入る。
        // それ以外は本体どおり（設定中なら取り消し／レイヴン所持なら単独設置）。
        [HarmonyPatch(typeof(EquipActionHandler), "OnSwitchModePerformed")]
        internal static class SwitchModeRetarget
        {
            private static bool Prefix(EquipActionHandler __instance)
            {
                try
                {
                    if (!Chain.UsesSwitchModeKey()) { return true; }
                    if (LWFTimeScale.IsPausing()) { return true; }
                    if (!Chain.IsHandlerUsable(__instance)) { return true; }
                    return !Chain.TryBeginRetarget();
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[switch] " + e);
                    return true;
                }
            }
        }

        // 運んでいる途中で荷下ろし地点を選び直したときの辻褄合わせ。
        //
        // 本体は飛ぶ先を出発時に固定し（BeginGoing で _tripExit / _tripExitPosition）、
        // 降ろす先は到着時に読み直す（BeginPutOrReturn で _putExit = _exit）。
        // 途中で差し替えると、古い場所へ飛んで新しい荷下ろし地点へ降ろすことになり、
        // 絵と中身がズレる。本体にはレイヴンの行き先を変える手立てが無いので、
        // この道は元々通らなかった。
        //
        // 繋ぎ直す直前に、今の道中を打ち切って帰投へ切り替える（Trip.HandOver）。
        [HarmonyPatch(typeof(TransporterObject), "TrySetExit")]
        internal static class RestartOnRetarget
        {
            private static void Prefix(TransporterObject __instance, TransporterExitObject exit)
            {
                try
                {
                    if (!Chain.IsRetargetRaven(__instance)) { return; }
                    if (exit == null) { return; }
                    if (__instance.GetExitObject() == exit) { return; }
                    Trip.HandOver(__instance, exit);
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[restart] " + e);
                }
            }
        }

        // 出発の直前。強制帰投で持ち帰った荷が残っている便は、まず積み込みへ回す。
        // 本体の仕事決めは運搬袋に荷があると積み込みを飛ばして即座に出発する
        // （本体では「荷を抱えたまま持ち場に居る」状態が無いため）。強制帰投が
        // その状態を作ったので、部分積載のまま飛ばず、満載にしてから出直させる。
        [HarmonyPatch(typeof(TransporterObject), "BeginGoing")]
        internal static class TopUpBeforeGoing
        {
            private static bool Prefix(TransporterObject __instance)
            {
                try { return !Trip.TryTopUp(__instance); }
                catch (Exception e) { RavenQolPlugin.Log.LogError("[topup] " + e); return true; }
            }
        }

        // 配送先を選び直したときの、元の荷下ろし地点の後始末。
        // 本体は繋ぐレイヴンが居なくなった荷下ろし地点を即座に片付けるが、
        // 行き先を変えただけで消えると中の荷ごと失われる。変更中だけ片付けを見送る。
        [HarmonyPatch(typeof(TransporterExitObject), "ReleaseIfNoLinkedTransporter")]
        internal static class KeepOldExit
        {
            private static bool Prefix()
            {
                try
                {
                    if (!RavenQolPlugin.KeepOldExit.Value) { return true; }
                    if (!Chain.IsRetargetingNow()) { return true; }
                    RavenQolPlugin.Log.LogInfo("[chain] kept the old dispatch port");
                    return false;
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[keep] " + e);
                    return true;
                }
            }
        }

        // 配送先の変更中の「取り消し」。本体の CancelPutExitMode は、レイヴンを置いた直後の
        // 設定モードを想定していて、取り消すとそのレイヴンを拾い上げてしまう。
        // 既に働いているレイヴンの行き先を選び直しているだけのときにそれをやられると、
        // 触媒に戻って線が消える。拾い上げない CompletePutExitMode へ振り替える。
        [HarmonyPatch(typeof(TransporterSummonProcessor), "CancelPutExitMode")]
        internal static class CancelKeepsRaven
        {
            private static bool Prefix(TransporterSummonProcessor __instance)
            {
                try
                {
                    if (!Chain.IsRetargeting(__instance)) { return true; }
                    __instance.CompletePutExitMode();
                    RavenQolPlugin.Log.LogInfo("[chain] retarget cancelled (raven unchanged)");
                    return false;
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[cancel] " + e);
                    return true;
                }
            }
        }

        // ボタンを離した合図。context.canceled のときだけがドラッグの終わり。
        [HarmonyPatch(typeof(EquipActionHandler), "OnMainAction")]
        internal static class MainActionEnd
        {
            private static void Postfix(InputAction.CallbackContext context)
            {
                try
                {
                    if (!context.canceled) { return; }
                    Chain.OnDragEnd();
                }
                catch (Exception e)
                {
                    RavenQolPlugin.Log.LogError("[end] " + e);
                }
            }
        }
    }

    /// <summary>
    /// 運搬の道中に割り込んで、行き先の差し替えを繋ぐ。
    ///
    /// StopWorking() で止めると ResetRendererToEntry() が走ってレイヴンが持ち場へ瞬間移動する。
    /// 絵として見苦しいので、本体の帰投（ReturningStart → ReturningMove → ReturningEnd）へ
    /// 差し替えて、今いる場所から飛んで帰らせる。帰り着けば本体が自分で次の便を組み直し、
    /// そのとき読む _exit は差し替え後のものになる。
    ///
    /// 触るのは全部 private なので Reflection。数フレームに一度どころか、
    /// 配送先を変えた瞬間にしか通らないので、速さは問題にならない。
    /// </summary>
    internal static class Trip
    {
        // TransporterObject.WorkState の並び
        private const int StateEvaluate = 0;
        private const int StatePickBeforeTransfer = 2;
        private const int StatePickAfterTransfer = 3;
        private const int StateGoingStart = 4;
        private const int StateReturningStart = 9;

        private static FieldInfo _fIsWorking;
        private static FieldInfo _fWorkState;
        private static FieldInfo _fRenderer;
        private static FieldInfo _fEntryPosition;
        private static FieldInfo _fTripExit;
        private static FieldInfo _fTripExitPosition;
        private static FieldInfo _fTripDistance;
        private static MethodInfo _mBeginReturning;
        private static bool _ready;

        // 運び出し（置き去りの荷を取りに行く）用
        private static Type _tWorkState;
        private static MethodInfo _mSetWorkState;
        private static FieldInfo _fAnimController;
        private static MethodInfo _mAnimWorkingStart;
        private static FieldInfo _fCarryingItemPreview;
        private static MethodInfo _mPreviewEnable;

        // 積み足し（強制帰投の荷を満載にしてから出直す）用
        private static FieldInfo _fSourceBinding;
        private static FieldInfo _fPickSourceSnapshot;
        private static MethodInfo _mAnimGather;
        private static PropertyInfo _pBindingSource;
        private static MethodInfo _mIsValidSourceBinding;
        private static MethodInfo _mIsTargetHasAny;
        private static bool _topupReady;

        /// <summary>強制帰投で荷を持ち帰った便。次の出発の前に積み足す。</summary>
        private static readonly HashSet<TransporterObject> TopUpPending = new HashSet<TransporterObject>();

        /// <summary>レイヴンごとの「取りに行く先」。配送先の変更で置き去りになった荷下ろし地点（古い順）。</summary>

        internal static void Init()
        {
            Type t = typeof(TransporterObject);
            BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            _fIsWorking = t.GetField("_isWorking", f);
            _fWorkState = t.GetField("_workState", f);
            _fRenderer = t.GetField("_renderer", f);
            _fEntryPosition = t.GetField("_entryPosition", f);
            _fTripExit = t.GetField("_tripExit", f);
            _fTripExitPosition = t.GetField("_tripExitPosition", f);
            _fTripDistance = t.GetField("_tripDistance", f);
            _mBeginReturning = t.GetMethod("BeginReturning", f);

            _ready = _fIsWorking != null && _fWorkState != null && _fRenderer != null
                && _fEntryPosition != null && _fTripExit != null && _fTripExitPosition != null
                && _fTripDistance != null && _mBeginReturning != null;
            if (!_ready)
            {
                RavenQolPlugin.Log.LogWarning(
                    "[trip] cannot read TransporterObject internals; mid-flight retarget will desync the visuals.");
            }

            _tWorkState = t.GetNestedType("WorkState", BindingFlags.NonPublic);
            _mSetWorkState = t.GetMethod("SetWorkState", f);
            _fAnimController = t.GetField("_animController", f);
            _mAnimWorkingStart = ((_fAnimController != null)
                ? _fAnimController.FieldType.GetMethod("SetAnimWorkingStart")
                : null);
            _fCarryingItemPreview = t.GetField("_carryingItemPreview", f);
            _mPreviewEnable = ((_fCarryingItemPreview != null)
                ? _fCarryingItemPreview.FieldType.GetMethod("Enable", new Type[] { typeof(bool) })
                : null);
            _fSourceBinding = t.GetField("_sourceBinding", f);
            _fPickSourceSnapshot = t.GetField("_pickSourceSnapshot", f);
            _mAnimGather = ((_fAnimController != null)
                ? _fAnimController.FieldType.GetMethod("SetAnimGather")
                : null);
            Type tBinding = t.GetNestedType("SourceBinding", BindingFlags.NonPublic);
            _pBindingSource = ((tBinding != null) ? tBinding.GetProperty("Source") : null);
            _mIsValidSourceBinding = t.GetMethod("IsValidSourceBinding", f);
            _mIsTargetHasAny = t.GetMethod("IsTargetHasAny", f);

            _topupReady = _ready && _tWorkState != null && _mSetWorkState != null
                && _fSourceBinding != null && _fPickSourceSnapshot != null
                && _pBindingSource != null && _mIsValidSourceBinding != null && _mIsTargetHasAny != null;
            if (!_topupReady)
            {
                RavenQolPlugin.Log.LogWarning(
                    "[trip] cannot read internals needed for top-up; forced-return trips head out partially loaded.");
            }
        }

        /// <summary>
        /// 強制帰投で持ち帰った荷が残っている便を、出発の代わりに積み込みへ回す。
        /// 一度だけ。積み終われば本体の流れがそのまま出発させる。
        /// 戻り値 true = 積み込みへ回した（本体の出発は走らせない）。
        /// </summary>
        internal static bool TryTopUp(TransporterObject raven)
        {
            if (!_topupReady || raven == null) { return false; }
            if (!TopUpPending.Remove(raven)) { return false; }

            // 積み込み明け（PickAfterTransfer）の出発は既に満載。
            // 仕事決め（Evaluate）から直に来た＝持ち帰った荷のままの便だけ回す。
            int state = Convert.ToInt32(_fWorkState.GetValue(raven));
            if (state != StateEvaluate) { return false; }

            // 満載なら足すものが無い
            if (!HasFreeSlot(raven.GetDeliveryInventory().GetDB())) { return false; }

            // 積むあてが有るか。チェーンでは荷は前段の荷下ろし地点（供給元）に居て、
            // レイヴンの手元は常に空。荷が動くのはレイヴンの積み込み動作の中だけなので、
            // 手元だけ見ると「積むものが無い」と誤る。本体の仕事決めと同じ目で両方を見る。
            // （C# 5 はタプルの要素名を読めないので Item1（hasAny）で受ける）
            object binding = _fSourceBinding.GetValue(raven);
            bool entryHas = raven.GetInventory().GetDB().GetAnyContainsIndex().Item1;
            bool sourceHas = false;
            if (!entryHas && binding != null
                && (bool)_mIsValidSourceBinding.Invoke(raven, new object[] { binding }))
            {
                object source = _pBindingSource.GetValue(binding, null);
                if (source != null)
                {
                    sourceHas = (bool)_mIsTargetHasAny.Invoke(raven, new object[] { source });
                }
            }
            // 積むあてが無ければ待たずにそのまま飛ぶ（持ち帰った分だけ先に届ける）
            if (!entryHas && !sourceHas) { return false; }

            // 本体の積み込みフェーズをそのまま使う。スナップショットも本体と同じ取り方
            _fPickSourceSnapshot.SetValue(raven, binding);
            CallAnim(raven, _mAnimGather);
            SetWorkState(raven, StatePickBeforeTransfer);
            RavenQolPlugin.Log.LogInfo("[chain] topping up before heading out");
            return true;
        }

        private static bool HasFreeSlot(InventoryDB db)
        {
            if (db == null) { return false; }
            int capacity = db.GetCellCapacity;
            List<int> keys = db.GetKeys();
            for (int i = 0; i < keys.Count; i++)
            {
                if (db.ItemCount(keys[i]) < capacity) { return true; }
            }
            return false;
        }

        private static void SetWorkState(TransporterObject raven, int state)
        {
            _mSetWorkState.Invoke(raven, new object[] { Enum.ToObject(_tWorkState, state) });
        }

        private static void CallAnim(TransporterObject raven, MethodInfo method)
        {
            if (_fAnimController == null || method == null) { return; }
            object anim = _fAnimController.GetValue(raven);
            if (anim == null) { return; }
            method.Invoke(anim, null);
        }

        private static void EnableCarryingPreview(TransporterObject raven)
        {
            if (_fCarryingItemPreview == null || _mPreviewEnable == null) { return; }
            object preview = _fCarryingItemPreview.GetValue(raven);
            if (preview == null) { return; }
            _mPreviewEnable.Invoke(preview, new object[] { true });
        }

        /// <summary>今の道中を打ち切って、新しい行き先へ繋ぐ。</summary>
        internal static void HandOver(TransporterObject raven, TransporterExitObject newExit)
        {
            if (!_ready || raven == null || newExit == null || !newExit.pivot) { return; }
            object working = _fIsWorking.GetValue(raven);
            if (!(working is bool) || !(bool)working) { return; }

            int state = Convert.ToInt32(_fWorkState.GetValue(raven));

            // 持ち場に居る（評価・待機・積み込み）。道中はまだ決まっていないので触らない。
            if (state <= StatePickAfterTransfer) { return; }

            // 既に帰投中。荷を降ろす場面は無いのでズレようがない。
            if (state >= StateReturningStart) { return; }

            if (state == StateGoingStart)
            {
                // 飛び立つ直前。まだ動いていないので、行き先を取り直すだけで足りる。
                Vector3 entry = (Vector3)_fEntryPosition.GetValue(raven);
                Vector3 target = newExit.pivot.transform.position;
                _fTripExit.SetValue(raven, newExit);
                _fTripExitPosition.SetValue(raven, target);
                _fTripDistance.SetValue(raven, Vector3.Distance(entry, target));
                RavenQolPlugin.Log.LogInfo("[chain] refreshed the destination before takeoff");
                return;
            }

            // 道中か、古い荷下ろし地点で降ろしている最中。今いる場所から持ち場へ飛んで帰す。
            GameObject renderer = _fRenderer.GetValue(raven) as GameObject;
            if (renderer == null) { return; }

            Vector3 here = renderer.transform.position;
            Vector3 home = (Vector3)_fEntryPosition.GetValue(raven);

            // 帰投は _tripExitPosition から _entryPosition へ飛ぶ（BeginMove）。
            // 出発点を今の位置に置き換えれば、瞬間移動せずにその場から帰る。
            _fTripExitPosition.SetValue(raven, here);
            _fTripDistance.SetValue(raven, Vector3.Distance(here, home));
            _mBeginReturning.Invoke(raven, null);
            TopUpPending.Add(raven);
            RavenQolPlugin.Log.LogInfo("[chain] turning around mid-flight, heading home");
        }
    }

    /// <summary>
    /// 中継線を敷く本体。状態は「今のドラッグ1本ぶん」だけ持つ。
    /// </summary>
    internal static class Chain
    {
        // SummonCatalystEquip の protected フィールド。
        // 数フレームに1度しか触らないので素の Reflection で足りる。
        private static FieldInfo _fHit;
        private static FieldInfo _fGrid;
        private static FieldInfo _fIndex;

        private static TransporterCatalystEquip _ravenEquip;
        private static TransporterExitCatalystEquip _exitEquip;
        private static EquipActionHandler _handler;

        private const float EquipLookupInterval = 0.5f;
        private static float _lastEquipLookup = float.NegativeInfinity;

        // 配送先を選び直している最中のレイヴン。取り消しで拾い上げさせないための目印。
        private static TransporterObject _retargetRaven;

        // ドラッグ1本ぶんの状態
        private static float _lastHold = -1f;
        private static bool _blocked;
        private static int _hops;
        private static bool _swallowedClick;

        // 同じ文句を毎フレーム出さないための間引き
        private const float SkipMessageInterval = 1.5f;
        private static string _lastSkipKey;
        private static float _lastSkipTime = float.NegativeInfinity;

        internal static void Init()
        {
            Type t = typeof(SummonCatalystEquip);
            BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _fHit = t.GetField("cursorHitChecker", f);
            _fGrid = t.GetField("gridManager", f);
            _fIndex = t.GetField("playerIndex", f);
            if (_fHit == null || _fGrid == null || _fIndex == null)
            {
                RavenQolPlugin.Log.LogError(
                    "[init] SummonCatalystEquip fields not found; game version mismatch?");
            }
        }

        internal static void SetRavenEquip(TransporterCatalystEquip e) { _ravenEquip = e; }

        internal static void SetExitEquip(TransporterExitCatalystEquip e) { _exitEquip = e; }

        /// <summary>
        /// 触媒インスタンスは生成の瞬間に掴むのが本筋だが、ScriptEngine の F6 で
        /// 入れ替えるとラン途中では二度と生成されず、静的が空のままになる。
        /// そのときは走っているシーンから引き直す。
        /// </summary>
        private static void EnsureEquips()
        {
            if (_ravenEquip != null && _exitEquip != null && _handler != null) { return; }

            // シーン探索は安くないので、見つからないうちは間隔を空ける
            float now = Time.unscaledTime;
            if (now - _lastEquipLookup < EquipLookupInterval) { return; }
            _lastEquipLookup = now;

            ClickActionManager clickManager = UnityEngine.Object.FindAnyObjectByType<ClickActionManager>();
            if (clickManager == null) { return; }

            BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo fHandler = typeof(ClickActionManager).GetField("_equipActionHandler", f);
            if (fHandler == null) { return; }
            EquipActionHandler handler = fHandler.GetValue(clickManager) as EquipActionHandler;
            if (handler == null) { return; }
            _handler = handler;

            if (_exitEquip == null)
            {
                FieldInfo fExit = typeof(EquipActionHandler).GetField("_putExitEquip", f);
                if (fExit != null) { _exitEquip = fExit.GetValue(handler) as TransporterExitCatalystEquip; }
            }
            if (_ravenEquip == null)
            {
                FieldInfo fService = typeof(EquipActionHandler).GetField("_equipTypeService", f);
                if (fService != null)
                {
                    EquipTypeService service = fService.GetValue(handler) as EquipTypeService;
                    if (service != null)
                    {
                        _ravenEquip = service.FetchIEquip("Summon-Transporter") as TransporterCatalystEquip;
                    }
                }
            }
        }

        // ---- ドラッグの生死 ----------------------------------------------

        internal static void TrackHold(float holdCounter)
        {
            // 押し直すと holdCounter は 0 に戻る。減っていたら別のドラッグ。
            if (holdCounter <= _lastHold) { ResetSession(); }
            _lastHold = holdCounter;
        }

        private static void ResetSession()
        {
            _lastHold = -1f;
            _blocked = false;
            _hops = 0;
            _swallowedClick = false;
            _lastSkipKey = null;
            _lastSkipTime = float.NegativeInfinity;
        }

        /// <summary>
        /// 「荷下ろし地点設定」中で修飾キーが押されているなら、押し込みを飲み込んで
        /// ドラッグの起点にする。飲み込んだことは覚えておく（引かずに離した場合、
        /// 本体どおり離した位置へ置いてやるため）。
        /// </summary>
        internal static bool TrySwallowClick()
        {
            if (!RavenQolPlugin.ChainEnabled.Value) { return false; }
            if (FindLinkingProcessor() == null) { return false; }
            _swallowedClick = true;
            RavenQolPlugin.Log.LogInfo("[chain] swallowed the press (drag origin)");
            return true;
        }

        internal static bool ShouldChain()
        {
            if (!RavenQolPlugin.ChainEnabled.Value) { return false; }
            if (_blocked) { return false; }
            // 暴走よけ。触媒の在庫でも止まるが、念のため頭を押さえておく
            if (_hops >= 64) { return false; }
            return FindLinkingProcessor() != null;
        }

        /// <summary>
        /// 今、盤面を直に触れる状態か。メニューや契約の窓が開いていれば false。
        /// カーソルの下の情報を出していいかの判断に使う。
        /// 本体側にひとつの旗が無いので、独立した3つの合図をすべて満たしたときだけ true にする。
        /// </summary>
        internal static bool IsMapInteractive()
        {
            // UI の上（メニューや窓が覆っている）
            if (PointerOverFinder.IsPointerOverUI) { return false; }

            // 道具を使える状態か（インベントリの窓が選ばれているか）
            EnsureEquips();
            if (_handler == null) { return false; }
            if (!IsHandlerUsable(_handler)) { return false; }

            // 盤面の入力そのものが切られていないか（契約の窓などが切る）
            PlayerActionMap map = PlayerActionMap.Instance;
            if (map == null) { return false; }
            InputAction main = map.GetMainAction();
            if (main == null || !main.enabled) { return false; }

            return true;
        }

        /// <summary>カーソルが何を指しているか。中身の表示から使う。</summary>
        internal static CursorHitChecker CurrentCursorHitChecker()
        {
            EnsureEquips();
            if (_exitEquip == null || _fHit == null) { return null; }
            return _fHit.GetValue(_exitEquip) as CursorHitChecker;
        }

        /// <summary>モードを問わず、今の処理係を返す。</summary>
        private static TransporterSummonProcessor FindProcessor()
        {
            EnsureEquips();
            if (_exitEquip == null || _fGrid == null) { return null; }
            GridManager gm = _fGrid.GetValue(_exitEquip) as GridManager;
            if (gm == null) { return null; }
            MapObjectPlacer placer = gm.MapObjectPlacer;
            if (placer == null) { return null; }
            return placer.TransporterSummonProcessor;
        }

        /// <summary>レイヴンに紐づく「荷下ろし地点設定」中のときだけ返す。Tab で入る単独モードは対象外。</summary>
        private static TransporterSummonProcessor FindLinkingProcessor()
        {
            TransporterSummonProcessor proc = FindProcessor();
            if (proc == null) { return null; }
            if (proc.CurrentPutExitMode != TransporterSummonProcessor.PutExitModeState.LinkingTransporter) { return null; }
            if (proc.GetEntryObject() == null) { return null; }
            return proc;
        }

        // ---- 配送先の変更 --------------------------------------------------

        /// <summary>このレイヴンが、今まさに行き先を選び直されている当人か。</summary>
        internal static bool IsRetargetRaven(TransporterObject raven)
        {
            if (raven == null || _retargetRaven != raven) { return false; }
            return IsRetargetingNow();
        }

        /// <summary>今この瞬間、既にあるレイヴンの行き先を選び直している最中か。</summary>
        internal static bool IsRetargetingNow()
        {
            if (_retargetRaven == null) { return false; }
            return IsRetargeting(FindProcessor());
        }

        /// <summary>この処理係が「既にあるレイヴンの行き先を選び直している」最中か。</summary>
        internal static bool IsRetargeting(TransporterSummonProcessor proc)
        {
            if (_retargetRaven == null || proc == null) { return false; }
            if (proc.CurrentPutExitMode != TransporterSummonProcessor.PutExitModeState.LinkingTransporter) { return false; }
            return proc.GetEntryObject() == _retargetRaven;
        }

        /// <summary>
        /// 配送先の変更が生きているか。キーは本体のキーコンフィグ
        /// 「荷下ろし地点設定切り替え」（GameActions.SwitchModeAction・既定 Tab）に相乗りする。
        /// 生のキーを見張る作りだとキーコンフィグの変更に追随できず、しかも本体の
        /// 入力コールバックが先に走って単独設置モードへ入ってしまうため。
        /// </summary>
        internal static bool UsesSwitchModeKey()
        {
            return RavenQolPlugin.RetargetEnabled.Value;
        }

        /// <summary>本体が Tab を受け付ける状態か（インベントリを触れる状態か）。</summary>
        internal static bool IsHandlerUsable(EquipActionHandler handler)
        {
            if (handler == null) { return false; }
            BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo fCanUse = typeof(EquipActionHandler).GetField("_canUse", f);
            if (fCanUse == null) { return true; }
            object value = fCanUse.GetValue(handler);
            return value is bool && (bool)value;
        }

        /// <summary>毎フレーム。目印の後始末。</summary>
        internal static void UpdateRetarget()
        {
            if (_retargetRaven == null) { return; }
            // モードが終わっていたら目印を落とす
            if (!IsRetargeting(FindProcessor())) { _retargetRaven = null; }
        }

        /// <summary>
        /// カーソルの下のレイヴンについて、本体の「荷下ろし地点設定」に入り直す。
        /// 入れたら true。false のときは呼び元が本体の動きへ譲ること。
        /// </summary>
        internal static bool TryBeginRetarget()
        {
            TransporterSummonProcessor proc = FindProcessor();
            if (proc == null) { return false; }
            if (proc.IsPutExitModeActive) { return false; }

            CursorHitChecker hit = _fHit.GetValue(_exitEquip) as CursorHitChecker;
            if (hit == null) { return false; }
            ChildGrid grid = hit.HitChildGrid;
            if (grid == null) { return false; }

            TransporterObject raven = hit.GetAdjustedMapObject(grid.GlobalAddress) as TransporterObject;
            if (raven == null) { return false; }
            ChildGrid ravenGrid = raven.GetAddressGrid();
            if (ravenGrid == null) { return false; }

            float range;
            if (!TryGetCarryingRange(out range)) { return false; }

            proc.CreateNewProcess(ravenGrid.GlobalAddress, range, raven);
            _retargetRaven = raven;
            RavenQolPlugin.Log.LogInfo("[chain] retarget started at " + ravenGrid.GlobalAddress);
            return true;
        }

        // ---- 1区間ぶん敷く ------------------------------------------------

        /// <summary>
        /// カーソルが1区間ぶん離れていたら「荷下ろし地点 → 次のレイヴン」を置く。
        /// 置く処理は非同期になりうるので、続きは Hop クラスの数珠繋ぎで進む。
        /// </summary>
        internal static UniTask<MapObject> Step(TransporterExitCatalystEquip exitEquip)
        {
            UniTask<MapObject> nothing = UniTask.FromResult<MapObject>(null);

            CursorHitChecker hit = _fHit.GetValue(exitEquip) as CursorHitChecker;
            GridManager gm = _fGrid.GetValue(exitEquip) as GridManager;
            InventoryIndex index = _fIndex.GetValue(exitEquip) as InventoryIndex;
            if (hit == null || gm == null || index == null) { return nothing; }

            MapObjectPlacer placer = gm.MapObjectPlacer;
            TransporterSummonProcessor proc = placer.TransporterSummonProcessor;
            TransporterObject raven = proc.GetEntryObject();
            if (raven == null) { return nothing; }

            ChildGrid ravenGrid = raven.GetAddressGrid();
            ChildGrid cursorGrid = hit.HitChildGrid;
            if (ravenGrid == null || cursorGrid == null) { return nothing; }

            Vector2Int from = ravenGrid.GlobalAddress;
            Vector2Int to = cursorGrid.GlobalAddress;

            int span = CurrentSpan();
            if (span < 1) { return nothing; }

            Vector2Int delta = new Vector2Int(to.x - from.x, to.y - from.y);

            // カーソルがまだ1区間ぶん離れていないなら何もしない。
            // （先回りして置くと、ちょっと引いただけで満額の区間が生えてしまう）
            if (Manhattan(delta) < span + 1) { return nothing; }

            // 荷下ろし地点は「カーソルの向きで、マンハッタン距離ちょうど span」の点。
            // 斜めに引けば斜めの角に落ちる＝運搬範囲のひし形の縁いっぱい。
            Vector2Int reach = ReachPoint(delta, span);
            Vector2Int exitAddr = new Vector2Int(from.x + reach.x, from.y + reach.y);

            // 次のレイヴンはその1つ先。荷下ろし地点の出力口が向く先でもある。
            Direction4 dir = DominantDirection(new Vector2Int(to.x - exitAddr.x, to.y - exitAddr.y));
            if (dir == Direction4.Invalid) { return nothing; }
            Vector2Int step = DirectionCalc.DIRECTION4_VECTOR2_INT[dir];
            Vector2Int nextAddr = new Vector2Int(exitAddr.x + step.x, exitAddr.y + step.y);

            // ここで駄目でも「今は置けない」だけ。カーソルが戻れば続きを敷ける。
            // 未所有の土地を一度なぞっただけでドラッグが死ぬのは行き過ぎだった。
            if (index.GetCurrentItemCount() <= 0) { Skip(null); return nothing; }
            if (!CanPlace(hit, exitAddr)) { Skip(BlockReason(hit, exitAddr)); return nothing; }
            if (!CanPlace(hit, nextAddr)) { Skip(BlockReason(hit, nextAddr)); return nothing; }
            if (_ravenEquip == null) { Skip(null); return nothing; }

            Hop hop = new Hop(exitEquip, hit, placer, proc, index, raven, exitAddr, nextAddr, dir);
            return hop.Start();
        }

        /// <summary>1区間ぶんの手順。ContinueWith で「置く → 続き」を繋ぐための入れ物。</summary>
        private sealed class Hop
        {
            private readonly TransporterExitCatalystEquip _exitEquip;
            private readonly CursorHitChecker _hit;
            private readonly MapObjectPlacer _placer;
            private readonly TransporterSummonProcessor _proc;
            private readonly InventoryIndex _index;
            private readonly TransporterObject _raven;
            private readonly Vector2Int _exitAddr;
            private readonly Vector2Int _nextAddr;
            private readonly Direction4 _facing;

            private TransporterExitObject _placedExit;

            internal Hop(TransporterExitCatalystEquip exitEquip, CursorHitChecker hit, MapObjectPlacer placer,
                TransporterSummonProcessor proc, InventoryIndex index, TransporterObject raven,
                Vector2Int exitAddr, Vector2Int nextAddr, Direction4 dir)
            {
                _exitEquip = exitEquip;
                _hit = hit;
                _placer = placer;
                _proc = proc;
                _index = index;
                _raven = raven;
                _exitAddr = exitAddr;
                _nextAddr = nextAddr;
                // 荷下ろし地点も次のレイヴンも、線の進む向きと逆を向く
                _facing = DirectionCalc.GetOpposite(dir);
            }

            internal UniTask<MapObject> Start()
            {
                _hit.SetAdjustedChildGridGlobalAddress(_exitAddr);
                UniTask<FamObject> task = Summon(_exitEquip.summonStrategyDistributor, _placer.PutDirection, _hit, _facing);
                // C# 5 はメソッドグループの戻り値でオーバーロードを選び分けられないので、
                // 受け取る側の型を明示して 1 つに絞る
                Func<FamObject, UniTask<MapObject>> next = OnExitPlaced;
                return task.ContinueWith<FamObject, MapObject>(next);
            }

            private UniTask<MapObject> OnExitPlaced(FamObject placed)
            {
                UniTask<MapObject> nothing = UniTask.FromResult<MapObject>(null);

                TransporterExitObject exit = placed as TransporterExitObject;
                if (exit == null)
                {
                    _hit.ClearAdjustedChildGridGlobalAddress();
                    Block("InvalidDestination");
                    return nothing;
                }
                if (!_raven.TrySetExit(exit))
                {
                    exit.DestroyPivot();
                    _hit.ClearAdjustedChildGridGlobalAddress();
                    Block("InvalidDestination");
                    return nothing;
                }
                _placedExit = exit;
                _proc.RecordAvailableExit(exit);
                // 先に閉じないと、次の CreateNewProcess が今のレイヴンを拾い上げてしまう
                _proc.CompletePutExitMode();

                // CompletePutExitMode が hit の狙いを消すので、ここで置き直す
                _hit.SetAdjustedChildGridGlobalAddress(_nextAddr);
                UniTask<FamObject> task = Summon(_ravenEquip.summonStrategyDistributor, _placer.PutDirection, _hit, _facing);
                Func<FamObject, MapObject> next = OnRavenPlaced;
                return task.ContinueWith<FamObject, MapObject>(next);
            }

            private MapObject OnRavenPlaced(FamObject placed)
            {
                _hit.ClearAdjustedChildGridGlobalAddress();

                TransporterObject next = placed as TransporterObject;
                if (next == null)
                {
                    Block("InvalidDestination");
                    return _placedExit;
                }
                UseOneCatalyst(_index);

                ChildGrid nextGrid = next.GetAddressGrid();
                if (nextGrid == null) { Block(null); return next; }

                float range;
                if (!TryGetCarryingRange(out range)) { range = CurrentSpan(); }
                _proc.CreateNewProcess(nextGrid.GlobalAddress, range, next);

                _hops++;
                RavenQolPlugin.Log.LogInfo("[chain] hop " + _hops
                    + " exit=" + _exitAddr + " raven=" + nextGrid.GlobalAddress);
                return next;
            }
        }

        // ---- ドラッグの終わり --------------------------------------------

        /// <summary>
        /// ボタンを離した瞬間。中継線が伸びていたら、最後の荷下ろし地点を置いて閉じる。
        /// カーソルの下にあるものを見て、繋げるなら繋げる方を優先する。
        /// </summary>
        internal static void OnDragEnd()
        {
            int hops = _hops;
            bool swallowed = _swallowedClick;
            ResetSession();

            // 中継線を1区間も敷いていないなら、本体の操作を邪魔していないので何もしない。
            // ただし押し込みを飲み込んでいた場合は、その代わりをここで果たす。
            if (hops <= 0 && !swallowed) { return; }

            TransporterSummonProcessor proc = FindLinkingProcessor();
            if (proc == null) { return; }

            CursorHitChecker hit = _fHit.GetValue(_exitEquip) as CursorHitChecker;
            GridManager gm = _fGrid.GetValue(_exitEquip) as GridManager;
            if (hit == null || gm == null) { return; }

            TransporterObject raven = proc.GetEntryObject();
            if (raven == null) { return; }
            ChildGrid ravenGrid = raven.GetAddressGrid();
            ChildGrid cursorGrid = hit.HitChildGrid;
            if (ravenGrid == null || cursorGrid == null) { return; }

            Vector2Int from = ravenGrid.GlobalAddress;
            Vector2Int to = cursorGrid.GlobalAddress;
            Vector2Int delta = new Vector2Int(to.x - from.x, to.y - from.y);

            // 行き先の判定は本体の運搬範囲そのままで見る（間隔の調整は関係ない）
            int maxSpan = MaxSpan();
            if (maxSpan < 1) { return; }

            MapObject onCursor = hit.GetAdjustedMapObject(to);

            // 1) カーソルの下が繋げる荷下ろし地点なら、新しく置かずにそこを行き先にする
            TransporterExitObject existing = onCursor as TransporterExitObject;
            if (existing != null && existing.CanLinkTransporter() && Manhattan(delta) <= maxSpan)
            {
                if (raven.TrySetExit(existing))
                {
                    proc.RecordAvailableExit(existing);
                    proc.CompletePutExitMode();
                    RavenQolPlugin.Log.LogInfo("[chain] connected to an existing dispatch port");
                    return;
                }
            }

            // 2) カーソルの下が受け口を持つもの（ポータル・水路・工房・別のレイヴン…）なら、
            //    その入力口の隣へ置いて噛み合わせる
            Vector2Int spot;
            Direction4 facing;
            if (TryFindFeedSpot(hit, onCursor, from, to, maxSpan, out spot, out facing))
            {
                new Finish(_exitEquip, hit, gm.MapObjectPlacer, proc, raven, facing).Start(spot);
                return;
            }

            // 3) それ以外は、離した位置（運搬範囲内に丸める）へ置く。置けなければ手前へ寄せる。
            int dist = Mathf.Min(Manhattan(delta), maxSpan);
            for (; dist >= 1; dist--)
            {
                Vector2Int reach = ReachPoint(delta, dist);
                Vector2Int candidate = new Vector2Int(from.x + reach.x, from.y + reach.y);
                if (!CanPlace(hit, candidate)) { continue; }

                // 出力口は「引いた向きの先」へ向けておく
                Direction4 outDir = DominantDirection(new Vector2Int(to.x - candidate.x, to.y - candidate.y));
                if (outDir == Direction4.Invalid) { outDir = DominantDirection(delta); }
                if (outDir == Direction4.Invalid) { return; }

                new Finish(_exitEquip, hit, gm.MapObjectPlacer, proc, raven,
                    DirectionCalc.GetOpposite(outDir)).Start(candidate);
                return;
            }
        }

        /// <summary>
        /// 受け口を持つものの隣で、荷下ろし地点を置けて運搬範囲にも収まるマスを探す。
        /// 返す facing は「その荷下ろし地点の出力口が相手を向く」向き
        /// （出力は背面なので、相手から見た候補マスの方角がそのまま向きになる）。
        /// </summary>
        private static bool TryFindFeedSpot(CursorHitChecker hit, MapObject target, Vector2Int from,
            Vector2Int targetAddr, int maxSpan, out Vector2Int address, out Direction4 facing)
        {
            address = new Vector2Int(0, 0);
            facing = Direction4.Invalid;

            ConnectableObject connectable = target as ConnectableObject;
            if (connectable == null) { return false; }

            int best = int.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                Direction4 d = (Direction4)i;
                // 相手の d 側が入力のときだけ噛み合う
                if (connectable.GetIOByDirection(d) != IOType.Input) { continue; }

                Vector2Int step = DirectionCalc.DIRECTION4_VECTOR2_INT[d];
                Vector2Int candidate = new Vector2Int(targetAddr.x + step.x, targetAddr.y + step.y);
                if (!CanPlace(hit, candidate)) { continue; }

                int dist = Manhattan(new Vector2Int(candidate.x - from.x, candidate.y - from.y));
                if (dist < 1 || dist > maxSpan || dist >= best) { continue; }

                best = dist;
                address = candidate;
                facing = d;
            }
            return facing != Direction4.Invalid;
        }

        /// <summary>線の終端。最後の荷下ろし地点を置いて閉じる。</summary>
        private sealed class Finish
        {
            private readonly TransporterExitCatalystEquip _exitEquip;
            private readonly CursorHitChecker _hit;
            private readonly MapObjectPlacer _placer;
            private readonly TransporterSummonProcessor _proc;
            private readonly TransporterObject _raven;
            private readonly Direction4 _facing;

            internal Finish(TransporterExitCatalystEquip exitEquip, CursorHitChecker hit, MapObjectPlacer placer,
                TransporterSummonProcessor proc, TransporterObject raven, Direction4 facing)
            {
                _exitEquip = exitEquip;
                _hit = hit;
                _placer = placer;
                _proc = proc;
                _raven = raven;
                _facing = facing;
            }

            internal void Start(Vector2Int address)
            {
                _hit.SetAdjustedChildGridGlobalAddress(address);
                UniTask<FamObject> task = Summon(_exitEquip.summonStrategyDistributor, _placer.PutDirection, _hit, _facing);
                Action<FamObject> next = OnPlaced;
                task.ContinueWith<FamObject>(next).Forget();
            }

            private void OnPlaced(FamObject placed)
            {
                _hit.ClearAdjustedChildGridGlobalAddress();
                TransporterExitObject exit = placed as TransporterExitObject;
                if (exit == null) { return; }
                if (!_raven.TrySetExit(exit)) { exit.DestroyPivot(); return; }
                _proc.RecordAvailableExit(exit);
                _proc.CompletePutExitMode();
                RavenQolPlugin.Log.LogInfo("[chain] placed the final dispatch port");
            }
        }

        // ---- 道具 ---------------------------------------------------------

        /// <summary>
        /// 本体が許す運搬範囲（マンハッタン距離）。契約で伸び縮みするので毎回引き直す。
        /// </summary>
        private static int MaxSpan()
        {
            float range;
            if (!TryGetCarryingRange(out range)) { return 0; }
            return (int)range;
        }

        /// <summary>1区間の長さ＝最大間隔。運搬範囲は契約で伸び縮みするので、置くたびに引き直す。</summary>
        private static int CurrentSpan()
        {
            return MaxSpan();
        }

        private static int Manhattan(Vector2Int delta)
        {
            return Mathf.Abs(delta.x) + Mathf.Abs(delta.y);
        }

        private static int Sign(int value)
        {
            if (value > 0) { return 1; }
            if (value < 0) { return -1; }
            return 0;
        }

        /// <summary>
        /// delta の向きへ、マンハッタン距離ちょうど dist の点。x と y へ比で割り振るので、
        /// 斜めに引けばひし形の斜めの縁に落ちる。dist が delta より遠ければ delta をそのまま返す。
        /// </summary>
        private static Vector2Int ReachPoint(Vector2Int delta, int dist)
        {
            int ax = Mathf.Abs(delta.x);
            int ay = Mathf.Abs(delta.y);
            int total = ax + ay;
            if (total <= 0 || dist <= 0) { return new Vector2Int(0, 0); }
            if (dist >= total) { return delta; }

            int px = Mathf.RoundToInt((float)dist * ax / total);
            if (px > ax) { px = ax; }
            int py = dist - px;
            if (py > ay) { py = ay; px = dist - py; }
            return new Vector2Int(px * Sign(delta.x), py * Sign(delta.y));
        }

        private static bool TryGetCarryingRange(out float carryingRange)
        {
            carryingRange = 0f;
            FamParamsManager manager = FamParamsManager.Instance;
            if (manager == null) { return false; }
            FamRuntimeParam param;
            if (!manager.TryGetParam(FamType.Transporter, FamParamName.CarryingRange, out param)) { return false; }
            if (param == null) { return false; }
            carryingRange = param.GetCurrentValue();
            return true;
        }

        private static Direction4 DominantDirection(Vector2Int delta)
        {
            if (delta.x == 0 && delta.y == 0) { return Direction4.Invalid; }
            if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
            {
                return (delta.x > 0) ? Direction4.East : Direction4.West;
            }
            return (delta.y > 0) ? Direction4.North : Direction4.South;
        }

        private static bool CanPlace(CursorHitChecker hit, Vector2Int address)
        {
            ChildGrid grid = hit.GetChildGridByGlobalAddress(address);
            if (grid == null) { return false; }
            if (!grid.IsOwn) { return false; }
            if (grid.IsOccupied) { return false; }
            return true;
        }

        private static string BlockReason(CursorHitChecker hit, Vector2Int address)
        {
            ChildGrid grid = hit.GetChildGridByGlobalAddress(address);
            if (grid == null) { return "HitGridNotOwn"; }
            if (!grid.IsOwn) { return "HitGridNotOwn"; }
            return "HitGridOccupied";
        }

        /// <summary>置く向きを一時的に差し替えて、本体の召喚をそのまま呼ぶ。</summary>
        private static UniTask<FamObject> Summon(SummonStrategyDistributor distributor,
            PutDirection putDirection, CursorHitChecker hit, Direction4 facing)
        {
            putDirection.SetTemporaryOverride(facing);
            try
            {
                // CallStrategyWithCheckAsync は最初の await より前に向きを読むので、
                // 呼び出した直後に戻して構わない（プレビューの矢印を汚さない）
                return distributor.CallStrategyWithCheckAsync(hit, 1, false);
            }
            finally
            {
                putDirection.ClearTemporaryOverride();
            }
        }

        private static void UseOneCatalyst(InventoryIndex index)
        {
            InventoryManager inv = InventoryManager.playerInventory;
            if (inv == null) { return; }
            InventoryWithdrawer withdrawer = inv.GetWithdrawer();
            if (withdrawer == null) { return; }
            withdrawer.WithdrawSingleBox(1, index.GetCurrentSelectedID());
        }

        /// <summary>
        /// 今フレームは置けない、というだけ。ドラッグは生かしたままにする。
        /// 毎フレーム同じ文句が出ると読めないので、同じ理由は少し間を置く。
        /// </summary>
        private static void Skip(string messageKey)
        {
            if (messageKey == null) { return; }
            float now = Time.unscaledTime;
            if (messageKey == _lastSkipKey && now - _lastSkipTime < SkipMessageInterval) { return; }
            _lastSkipKey = messageKey;
            _lastSkipTime = now;
            Tell(messageKey);
        }

        /// <summary>
        /// このドラッグはもう続けられない。途中まで置いてしまって辻褄が合わなくなったときだけ。
        /// </summary>
        private static void Block(string messageKey)
        {
            _blocked = true;
            Tell(messageKey);
        }

        private static void Tell(string messageKey)
        {
            if (messageKey == null) { return; }
            MessageSpawner spawner = MessageSpawner.Instance;
            if (spawner == null) { return; }
            spawner.SpawnFailed(messageKey).Forget();
        }
    }
}
