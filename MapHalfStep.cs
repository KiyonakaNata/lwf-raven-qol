// LWF Raven QoL — マップビューの半歩移動
//
// 本体のマップビューは親マス（20 unit 四方）の中央にしか止まれず、再投下の着地点も
// その中央に固定されている。境界のそばで作業したいときに、中央 → 境界 → 次の中央 と
// 半歩ずつ止まれるようにする。
//
// -- 境界に止まれる条件 --------------------------------------------------
// 境界に関わる親マス（1軸なら2枚、角なら4枚）が全部所有済みのときだけ。
// 未所有が絡む境界は本体どおり隣の中央へ飛ぶ。こう絞ると、中間地点では購入の余地が
// 最初から無く（購入UIはもともと所有済み表示になる）、着地点も必ず所有地の内側に収まる。
//
// -- 本体への割り込み ----------------------------------------------------
//   GridIndex.Move                    … 半歩で済む移動は本体に渡さず、視点だけ動かす
//   BirdsEyeCameraManagerMono.MoveGrid … 本体が親マスを動かしたら半歩の状態を整える
//   BirdsEyeCameraManagerMono.FinalizeClosedView … 閉じたら半歩を忘れる
//   DropMomokoPositionCalculator.Calculate … 着地点に半歩ぶん（10 unit）を足す
//
// 中間地点では左の一覧（採掘ポイント等）を隠す。あれは「今いる親マス」の情報なので、
// 境界にいるときに出しておくと片側の話しか出ず紛らわしい（HudTweaks.SetMidpointHold）。
using System;
using System.Reflection;
using HarmonyLib;
using Input.PlayerAction;
using Map;
using Map.Ownership;
using Sounds;
using UI.LandPurchaseUI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LwfRavenQol
{
    internal static class MapHalfStep
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private const float HalfTile = GridManager.ParentGridSize * 0.5f;

        // 各軸 0 か 1。1 ＝ その軸の +側の境界（index と index+1 の間）に居る
        private static Vector2Int _offset = new Vector2Int(0, 0);
        // 本体に親マスを動かしてもらった直後の MoveGrid で、この値を採る（普段は 0 に戻す）
        private static bool _pendingOnce;
        private static Vector2Int _pending = new Vector2Int(0, 0);

        private static FieldInfo _fGridIndex;
        private static FieldInfo _fGridManager;
        private static FieldInfo _fCameraMover;
        private static bool _ready;

        internal static void Init()
        {
            Type t = typeof(BirdsEyeCameraManagerMono);
            _fGridIndex = t.GetField("_gridIndex", F);
            _fGridManager = t.GetField("_gridManager", F);
            _fCameraMover = t.GetField("_cameraMover", F);
            _ready = _fGridIndex != null && _fGridManager != null && _fCameraMover != null;
            if (!_ready)
            {
                RavenQolPlugin.Log.LogWarning("[map] BirdsEyeCameraManagerMono fields not found; half-step movement disabled.");
            }
        }

        internal static bool AtMidpoint { get { return _offset.x != 0 || _offset.y != 0; } }

        // ---- 押しっぱなしで連続移動 ---------------------------------------------
        //
        // 本体は Move の started（押した瞬間）でしか親マスを動かさない。半歩を入れて刻みが
        // 倍になったので、押しっぱなしで自動連打する。最初の1歩は本体が出すので、
        // こちらは少し待ってから一定間隔で足す。押したまま向きを変えたときは本体が
        // started を出さないので、その場で1歩出す。

        private const float RepeatDelay = 0.35f;
        private const float RepeatInterval = 0.12f;
        private static Vector2Int _heldDir = new Vector2Int(0, 0);
        private static float _nextRepeat;
        private static FieldInfo _fClosing;

        internal static void Update()
        {
            Vector2Int zero = new Vector2Int(0, 0);
            if (!_ready || !RavenQolPlugin.MapEnabled.Value) { _heldDir = zero; return; }
            BirdsEyeCameraManagerMono be = HudTweaks.BirdsEye;
            if (be == null || !be || !be.IsOnBirdsEyeView() || IsClosing(be)) { _heldDir = zero; return; }

            PlayerActionMap map = PlayerActionMap.Instance;
            InputAction move = map == null ? null : map.GetMoveAction();
            Vector2Int dir = zero;
            if (move != null && move.enabled) { dir = Quantize(move.ReadValue<Vector2>()); }
            if (dir == zero) { _heldDir = zero; return; }

            float now = Time.unscaledTime;
            if (dir != _heldDir)
            {
                bool fromRest = (_heldDir == zero);
                _heldDir = dir;
                _nextRepeat = now + RepeatDelay;
                if (!fromRest) { StepHeld(be, dir); }
                return;
            }
            int guard = 0;
            while (now >= _nextRepeat && guard++ < 3)
            {
                StepHeld(be, dir);
                _nextRepeat += RepeatInterval;
            }
        }

        private static void StepHeld(BirdsEyeCameraManagerMono be, Vector2Int dir)
        {
            GridIndex gridIndex = _fGridIndex.GetValue(be) as GridIndex;
            if (gridIndex != null) { gridIndex.Move(dir); }   // 半歩の判定（MovePatch）を通る
        }

        /// <summary>本体の OnMoveStarted と同じ丸め。縦優先で1軸に落とす。</summary>
        private static Vector2Int Quantize(Vector2 v)
        {
            if (Mathf.Abs(v.y) >= Mathf.Abs(v.x) && !Mathf.Approximately(v.y, 0f))
            {
                return new Vector2Int(0, v.y > 0f ? 1 : -1);
            }
            if (Mathf.Approximately(v.x, 0f)) { return new Vector2Int(0, 0); }
            return new Vector2Int(v.x > 0f ? 1 : -1, 0);
        }

        private static bool IsClosing(BirdsEyeCameraManagerMono be)
        {
            if (_fClosing == null) { _fClosing = typeof(BirdsEyeCameraManagerMono).GetField("_isClosingBirdsEyeView", F); }
            if (_fClosing == null) { return false; }
            object v = _fClosing.GetValue(be);
            return v is bool && (bool)v;
        }

        internal static void Reset()
        {
            _offset = new Vector2Int(0, 0);
            _pendingOnce = false;
            HudTweaks.SetMidpointHold(false);
        }

        private static bool Enabled()
        {
            return _ready && RavenQolPlugin.MapEnabled.Value && RavenQolPlugin.MapHalfStepEnabled.Value;
        }

        // ---- 本体への割り込み --------------------------------------------------

        [HarmonyPatch(typeof(GridIndex), "Move")]
        internal static class MovePatch
        {
            private static bool Prefix(GridIndex __instance, Vector2Int moveDirection)
            {
                try { return !TryHandleMove(__instance, moveDirection); }
                catch (Exception e) { RavenQolPlugin.Log.LogError("[map] " + e); return true; }
            }
        }

        [HarmonyPatch(typeof(BirdsEyeCameraManagerMono), "MoveGrid")]
        internal static class MoveGridPatch
        {
            private static void Postfix(BirdsEyeCameraManagerMono __instance)
            {
                try { OnGridMoved(__instance); }
                catch (Exception e) { RavenQolPlugin.Log.LogError("[map] " + e); }
            }
        }

        [HarmonyPatch(typeof(BirdsEyeCameraManagerMono), "FinalizeClosedView")]
        internal static class ClosePatch
        {
            private static void Postfix() { Reset(); }
        }

        [HarmonyPatch(typeof(DropMomokoPositionCalculator), "Calculate")]
        internal static class DropPatch
        {
            private static void Postfix(ref Vector3 __result)
            {
                if (!AtMidpoint || !Enabled()) { return; }
                __result += new Vector3(_offset.x * HalfTile, 0f, _offset.y * HalfTile);
            }
        }

        // ---- 半歩の判断 -----------------------------------------------------------

        /// <summary>true ＝ こちらで処理した（本体の Move は走らせない）。</summary>
        private static bool TryHandleMove(GridIndex gridIndex, Vector2Int d)
        {
            _pendingOnce = false;   // 前回の予約が空振りしていたら忘れる
            if (!Enabled()) { return false; }
            if ((d.x != 0) == (d.y != 0)) { return false; }   // 1軸ずつしか来ない前提。違えば本体に任せる

            BirdsEyeCameraManagerMono be = HudTweaks.BirdsEye;
            if (be == null || !be || !be.IsOnBirdsEyeView()) { return false; }
            if (!ReferenceEquals(gridIndex, _fGridIndex.GetValue(be))) { return false; }
            GridManager gm = _fGridManager.GetValue(be) as GridManager;
            if (gm == null) { return false; }

            Vector2Int index = gridIndex.GetCurrent();
            bool xAxis = d.x != 0;
            int sign = xAxis ? d.x : d.y;
            int here = xAxis ? _offset.x : _offset.y;

            if (sign > 0)
            {
                if (here == 0)
                {
                    // 中央 → +側の境界。関わるマスが全部所有済みのときだけ
                    Vector2Int next = With(_offset, xAxis, 1);
                    if (!AllOwned(gm, index, next)) { return false; }
                    _offset = next;
                    Refocus(be, gm, index);
                    return true;
                }
                // 境界 → 隣の中央。本体に動かしてもらい、他軸の半歩は残せるなら残す
                Vector2Int target = index + d;
                if (!CanMove(gm, target)) { return true; }
                Vector2Int keep = With(_offset, xAxis, 0);
                if (!AllOwned(gm, target, keep)) { keep = new Vector2Int(0, 0); }
                _pending = keep;
                _pendingOnce = true;
                return false;
            }

            if (here == 1)
            {
                // 境界 → この親マスの中央
                _offset = With(_offset, xAxis, 0);
                Refocus(be, gm, index);
                return true;
            }
            // 中央 → −側の境界 ＝ 隣（index+d）の +側の境界。本体に隣へ動かしてもらってから半歩を乗せる
            Vector2Int prev = index + d;
            if (!CanMove(gm, prev)) { return false; }
            Vector2Int want = With(_offset, xAxis, 1);
            if (AllOwned(gm, prev, want))
            {
                _pending = want;
                _pendingOnce = true;
            }
            return false;
        }

        /// <summary>本体が親マスを動かした直後。予約があればその半歩、無ければ中央。</summary>
        private static void OnGridMoved(BirdsEyeCameraManagerMono be)
        {
            if (_pendingOnce)
            {
                _pendingOnce = false;
                _offset = _pending;
            }
            else
            {
                _offset = new Vector2Int(0, 0);
            }
            HudTweaks.SetMidpointHold(AtMidpoint);
            if (!AtMidpoint || !Enabled()) { return; }

            GridManager gm = _fGridManager.GetValue(be) as GridManager;
            GridIndex gridIndex = _fGridIndex.GetValue(be) as GridIndex;
            if (gm == null || gridIndex == null) { return; }
            Refocus(be, gm, gridIndex.GetCurrent(), false);
        }

        private static void Refocus(BirdsEyeCameraManagerMono be, GridManager gm, Vector2Int index)
        {
            Refocus(be, gm, index, true);
        }

        private static void Refocus(BirdsEyeCameraManagerMono be, GridManager gm, Vector2Int index, bool playSound)
        {
            ParentGrid grid = gm.GetParentGrid(index);
            if (grid == null) { return; }
            GameObject obj = grid.GetObject();
            if (obj == null) { return; }
            BirdsEyeCameraMover mover = _fCameraMover.GetValue(be) as BirdsEyeCameraMover;
            if (mover == null) { return; }

            Vector3 focus = obj.transform.position + new Vector3(_offset.x * HalfTile, 0f, _offset.y * HalfTile);
            mover.SetFocus(focus);
            HudTweaks.SetMidpointHold(AtMidpoint);
            if (playSound)
            {
                UISoundPlayer player = UISoundPlayer.Instance;
                if (player != null) { player.PlayRotateSE(true); }
            }
        }

        // ---- 道具 ---------------------------------------------------------------

        private static Vector2Int With(Vector2Int v, bool xAxis, int value)
        {
            return xAxis ? new Vector2Int(value, v.y) : new Vector2Int(v.x, value);
        }

        /// <summary>本体の GridIndex.CanMove と同じ判定（所有済みか、買える土地）。</summary>
        private static bool CanMove(GridManager gm, Vector2Int address)
        {
            ParentGrid grid = gm.GetParentGrid(address);
            if (grid == null) { return false; }
            return grid.IsOwn || grid.CanPurchase;
        }

        /// <summary>半歩の位置に関わる親マス（offset の軸ごとに index と index+1）が全部所有済みか。</summary>
        private static bool AllOwned(GridManager gm, Vector2Int index, Vector2Int offset)
        {
            for (int dx = 0; dx <= offset.x; dx++)
            {
                for (int dy = 0; dy <= offset.y; dy++)
                {
                    ParentGrid grid = gm.GetParentGrid(new Vector2Int(index.x + dx, index.y + dy));
                    if (grid == null || !grid.IsOwn) { return false; }
                }
            }
            return true;
        }
    }
}
