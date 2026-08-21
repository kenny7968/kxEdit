global using System;
global using System.Threading;
global using System.Windows.Forms;
global using kxEdit.Core.Buffers;
global using kxEdit.Editor;
global using Xunit;

// WinForms UI テストは STA スレッド上で走るが、xUnit v2 は既定でテストクラス毎に並列実行するため、
// 複数の STA スレッドが同時に走る=Task 11 の ClipboardTests が触るシステムクリップボードは
// プロセス横断のグローバル資源なため、他クラスの Sta.Run と並列実行されると Clipboard.SetText /
// GetText が空文字列を返す/失敗するフレークが発生する。テスト資産全体で並列化を無効化して回避する
// (WinForms テストの定石)。Editor テスト集は 100 件程度で全体 1〜2 秒=直列化のコストは許容範囲。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
