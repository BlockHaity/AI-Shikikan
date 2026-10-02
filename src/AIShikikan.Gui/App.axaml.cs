using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Worker;
using AIShikikan.Gui.ViewModels;
using AIShikikan.Gui.Views;

namespace AIShikikan.Gui;

public partial class App : Application
{
    public static ThemeService ThemeService { get; private set; } = null!;
    public static I18nService I18nService { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.EnsureDirectoriesExist();

        ThemeService = new ThemeService();
        I18nService = new I18nService();
        I18nService.LanguageChanged += (_, culture) => AIShikikan.Gui.Resources.Strings.Culture = culture;

        RequestedThemeVariant = ThemeService.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        I18nService.SetLanguage(ThemeService.Language);
        AIShikikan.Gui.Resources.Strings.Culture = I18nService.CurrentCulture;

        ThemeService.FontChanged += (_, font) => ApplyCustomFont(font);
        ThemeService.MonoFontChanged += (_, font) => ApplyMonoFont(font);
        ApplyCustomFont(ThemeService.CustomFont);
        ApplyMonoFont(ThemeService.MonoFont);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(ThemeService),
            };

            // 关掉全部 Worker 子进程的唯一钩子: 不接的话它们会变孤儿, 继续占着工作目录的 git 写锁。
            // 为什么不用 ShutdownRequested: 那个事件是「可以被取消的关闭请求」, 用户按取消就不会退出,
            // 而 Worker 的关闭代价(几毫秒到十几秒)不该压在可能反复取消的路径上。
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 退出路径: 关掉全部 Worker 子进程, 再释放会话注册表。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须等</b>: 这里放任后台任务(「_ = ...」)等于没关 ——
    /// <c>Program.cs</c> 的 finally 紧接着就是进程结束, 任务根本没机会跑完。
    /// 所以同步等待, 但<b>宁可超时也不抛</b>: 退出流程里抛出的异常会变成未处理异常,
    /// 而窗口此时已在销毁, 用户看到的是「关窗时闪退」。</para>
    /// <para><b>等待预算的来历</b>: 每个 Worker 自带两道闸门 —— 优雅关闭
    /// <see cref="WorkerProtocol.ShutdownTimeout"/>(5s)+ 释放等待(再 5s), 且全部 Worker 并行关闭,
    /// 故取 2 倍再留 1s 余量。超时的兜底是 Worker 自己的「父进程消失」自检。</para>
    /// <para>⚠ <c>Program.cs</c> 的 finally 随后还会 <c>Log.Flush()</c>; 那之前的日志会被静默丢弃,
    /// 所以这里只把异常记下来、不指望用户能看到 —— 关键动作(关进程)本身不依赖日志。</para>
    /// </remarks>
    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (CommanderRuntime.Instance is not { } runtime) return;

        try
        {
            var workers = runtime.Workers;
            if (workers is not null)
            {
                var budget = TimeSpan.FromSeconds(WorkerProtocol.ShutdownTimeout.TotalSeconds * 2 + 1);
                // AsTask(): ShutdownAllAsync 返回 ValueTask, 同步等待它需要一个 Task 句柄。
                workers.ShutdownAllAsync(CancellationToken.None).AsTask().Wait(budget);
            }
        }
        catch (Exception ex)
        {
            AIShikikan.Core.Logging.Log.Warn("Boot", ex, "退出时关闭 Worker 失败, 继续退出");
        }

        try
        {
            // 释放会话运行时(此前无生产调用方): 正在跑的回合随会话释放终止
            runtime.Sessions.Dispose();
        }
        catch (Exception ex)
        {
            AIShikikan.Core.Logging.Log.Warn("Boot", ex, "退出时释放会话运行时失败, 继续退出");
        }
    }

    /// <summary>应用自定义字体(标准字体): 以逗号分隔的 fallback 列表, 保证中文字形回退到 HarmonyOS Sans SC。</summary>
    private static void ApplyCustomFont(string? font)
    {
        if (Application.Current is not { } app) return;

        var family = string.IsNullOrWhiteSpace(font) || font == "系统默认"
            ? "avares://AIShikikan.Gui/Assets/Fonts/#HarmonyOS Sans SC"
            : $"{font}, HarmonyOS Sans SC";

        app.Resources["ContentControlThemeFontFamily"] = new FontFamily(family);
    }

    /// <summary>应用自定义等宽字体: 默认使用内置 CaskaydiaCove Nerd Font Mono, 中文字形回退 HarmonyOS Sans SC。</summary>
    private static void ApplyMonoFont(string? font)
    {
        if (Application.Current is not { } app) return;

        var family = string.IsNullOrWhiteSpace(font) || font == "系统默认"
            ? "avares://AIShikikan.Gui/Assets/Fonts/#CaskaydiaCove Nerd Font Mono"
            : $"{font}, HarmonyOS Sans SC, monospace";

        app.Resources["MonoThemeFontFamily"] = new FontFamily(family);
    }
}
