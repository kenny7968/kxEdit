global using System;
global using System.Threading;
global using System.Windows.Forms;
global using kxEdit.App;
global using kxEdit.Editor;
global using Xunit;

// App.Tests も Editor.Tests と同じく実 WinForms コントロール(EditorControl/TabControl)を
// STA スレッド上で生成する。xUnit v2 の既定並列実行だと複数 STA スレッドが同時に走り、
// フォーカス・アクティブ化などプロセス/デスクトップ横断の資源でフレークし得るため、
// Editor.Tests と同じ定石で並列化を無効化する(テスト数は小規模で直列コストは許容範囲)。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
