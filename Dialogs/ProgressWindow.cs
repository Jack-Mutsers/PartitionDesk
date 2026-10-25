using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PartitionDesk.Core;

namespace PartitionDesk.Dialogs;

/// <summary>Runs a long operation on a worker thread and shows its steps and copy progress.</summary>
public sealed class ProgressWindow : Window
{
    readonly TextBlock _step = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar _bar = new() { Height = 8, IsIndeterminate = true };
    readonly TextBlock _detail = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox _log = new() { IsReadOnly = true, Height = 110, Margin = new Thickness(0, 12, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
    readonly Button _cancel = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), MinWidth = 90, Padding = new Thickness(14, 6, 14, 6) };
    readonly CancellationTokenSource _cts = new();
    readonly Stopwatch _clock = new();
    bool _done;

    public Exception? Error { get; private set; }

    ProgressWindow(Window owner, string title, bool cancellable)
    {
        Owner = owner;
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(_step);
        panel.Children.Add(_bar);
        panel.Children.Add(_detail);
        panel.Children.Add(_log);
        if (cancellable) panel.Children.Add(_cancel);
        else
        {
            var warning = new TextBlock { Text = "This step cannot be interrupted. Do not turn off the computer.", Margin = new Thickness(0, 12, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            panel.Children.Add(warning);
        }
        Content = panel;
        _cancel.Click += (_, _) => { _cancel.IsEnabled = false; _cts.Cancel(); Log("Cancelling…"); };
        Closing += (_, e) => e.Cancel = !_done;
    }

    void Log(string line)
    {
        _step.Text = line;
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n");
        _log.ScrollToEnd();
    }

    void Report(CopyProgress p)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        double secs = Math.Max(0.5, _clock.Elapsed.TotalSeconds);
        double rate = p.Done / secs;
        var remaining = rate > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / rate) : TimeSpan.Zero;
        _detail.Text = $"{Format.Bytes(p.Done)} of {Format.Bytes(p.Total)} · {Format.Bytes((ulong)rate)}/s · {remaining:hh\\:mm\\:ss} left";
    }

    /// <summary>Runs the work and returns the error, if any. The dialog closes when it finishes.</summary>
    public static Exception? Run(Window owner, string title, bool cancellable,
        Action<IProgress<CopyProgress>, IProgress<string>, CancellationToken> work)
    {
        var w = new ProgressWindow(owner, title, cancellable);
        var copy = new Progress<CopyProgress>(w.Report);
        var log = new Progress<string>(w.Log);
        w.Loaded += async (_, _) =>
        {
            w._clock.Start();
            try { await Task.Run(() => work(copy, log, w._cts.Token)); }
            catch (Exception ex) { w.Error = ex; }
            w._done = true;
            w.Close();
        };
        w.ShowDialog();
        return w.Error;
    }
}
