// 開発用（build.ps1 -Dev のときだけ入る）。ゲームの中から画面を撮る。
//
// 外から撮る shot.ps1（GDI）は、撮った絵が画面で見る色より白っぽくなることがあった
// （本体のクリーム色 245,234,220 が 254,254,235 で写る。2026-11-12）。
// ScreenCapture はゲームが描いた絵そのものを書き出すので、色がずれない。
//
// 合図は %TEMP%\lwf_shot.txt（1行目＝書き出し先の PNG・2行目＝待つ秒数）。
// 見つけたら消して、秒数だけ待ってから撮る。キーを使わないので、Alt を押したまま撮れる。

#if DEV
using System;
using System.IO;
using UnityEngine;

namespace LwfRavenQol
{
    internal static class DevShot
    {
        private static readonly string Trigger = Path.Combine(Path.GetTempPath(), "lwf_shot.txt");
        private static float _nextPoll;
        private static float _shootAt = -1f;
        private static string _out;

        internal static void Update()
        {
            float now = Time.unscaledTime;
            if (_shootAt >= 0f)
            {
                if (now < _shootAt) { return; }
                _shootAt = -1f;
                ScreenCapture.CaptureScreenshot(_out);
                RavenQolPlugin.Log.LogInfo("[dev] shot " + _out);
                // 重なり順の調べ物用：どのカメラが何を、どの順で描いているか
                foreach (Camera c in Camera.allCameras)
                {
                    RavenQolPlugin.Log.LogInfo("[dev] camera " + c.name + " depth=" + c.depth
                        + " mask=0x" + c.cullingMask.ToString("X8") + " ortho=" + c.orthographic
                        + " clear=" + c.clearFlags + " target=" + (c.targetTexture ? c.targetTexture.name : "-"));
                }
                return;
            }
            if (now < _nextPoll) { return; }
            _nextPoll = now + 0.5f;
            if (!File.Exists(Trigger)) { return; }

            string[] lines = File.ReadAllLines(Trigger);
            File.Delete(Trigger);
            if (lines.Length < 1 || string.IsNullOrEmpty(lines[0])) { return; }
            float delay = 8f;
            if (lines.Length > 1) { float.TryParse(lines[1], out delay); }
            _out = lines[0].Trim();
            _shootAt = now + delay;
            RavenQolPlugin.Log.LogInfo("[dev] shot in " + delay + "s");
        }
    }
}
#endif
