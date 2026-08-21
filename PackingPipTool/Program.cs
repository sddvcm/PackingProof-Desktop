using System;
using System.Windows.Forms;

namespace PackingPipTool;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // 默认未捕获异常弹窗，避免工具静默崩溃
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show("工具发生未捕获异常：" + e.Exception.Message,
                "合并画中画工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.Run(new MainForm());
    }
}