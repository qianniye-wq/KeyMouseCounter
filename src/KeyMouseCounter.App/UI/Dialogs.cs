using System.Drawing;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.UI;

/// <summary>指定日期（§7.1 日期选择："今日/昨日/指定日期"）。</summary>
internal sealed class DatePickerDialog : Form
{
    private readonly MonthCalendar _cal = new();

    public DatePickerDialog(DateOnly initial, DateOnly max)
    {
        Text = "选择日期";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);
        ClientSize = new Size(316, 300);

        _cal.Dock = DockStyle.Fill;
        _cal.MaxDate = max.ToDateTime(TimeOnly.MinValue);
        _cal.MinDate = new DateTime(2000, 1, 1);
        _cal.MaxSelectionCount = 1;
        _cal.SelectionStart = initial.ToDateTime(TimeOnly.MinValue);
        _cal.DateSelected += (_, _) => { if (_cal.SelectionStart.Date > _cal.MaxDate.Date) _cal.SetDate(_cal.MaxDate); };

        var ok = new FlatButton { Text = "确定", Width = 86, Height = 28, Primary = true, DialogResult = DialogResult.OK };
        var cancel = new FlatButton { Text = "取消", Width = 86, Height = 28, DialogResult = DialogResult.Cancel };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 42,
            Padding = new Padding(0, 7, 10, 0),
            BackColor = Color.Transparent
        };
        bar.Controls.Add(ok);
        bar.Controls.Add(cancel);

        Controls.Add(_cal);
        Controls.Add(bar);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public DateOnly SelectedDate => DateOnly.FromDateTime(_cal.SelectionStart);
}

/// <summary>自定义区间（§7.1：最多 366 天）。</summary>
internal sealed class RangeDialog : Form
{
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly Label _hint = new();
    private readonly FlatButton _ok = new() { Text = "确定", Width = 86, Height = 28, Primary = true };

    public RangeDialog(DateOnly from, DateOnly to)
    {
        Text = "自定义区间";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);
        ClientSize = new Size(330, 168);

        var today = DateTime.Today;
        foreach (var p in new[] { _from, _to })
        {
            p.Format = DateTimePickerFormat.Custom;
            p.CustomFormat = "yyyy-MM-dd";
            p.Width = 132;
            p.MaxDate = today;
            p.MinDate = new DateTime(2000, 1, 1);
        }
        _from.Value = from.ToDateTime(TimeOnly.MinValue);
        _to.Value = to.ToDateTime(TimeOnly.MinValue);

        _hint.ForeColor = Theme.Current.Muted;
        _hint.AutoSize = false;
        _hint.Width = 290;
        _hint.Height = 20;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(12),
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        grid.Controls.Add(new Label { Text = "起始", ForeColor = Theme.Current.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_from, 1, 0);
        grid.Controls.Add(new Label { Text = "结束", ForeColor = Theme.Current.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        grid.Controls.Add(_to, 3, 0);
        grid.SetColumnSpan(_hint, 4);
        grid.Controls.Add(_hint, 0, 1);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 42,
            Padding = new Padding(0, 6, 12, 0),
            BackColor = Color.Transparent
        };
        var cancel = new FlatButton { Text = "取消", Width = 86, Height = 28, DialogResult = DialogResult.Cancel };
        bar.Controls.Add(_ok);
        bar.Controls.Add(cancel);

        Controls.Add(grid);
        Controls.Add(bar);
        AcceptButton = _ok;
        CancelButton = cancel;

        _from.ValueChanged += (_, _) => ValidateRange();
        _to.ValueChanged += (_, _) => ValidateRange();
        try { Theme.Changed += Repaint; } catch { }
        Validate();
    }

    private void Repaint()
    {
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Invalidate(true);
    }

    private bool ValidateRange()
    {
        var f = DateOnly.FromDateTime(_from.Value);
        var t = DateOnly.FromDateTime(_to.Value);
        if (t < f)
        {
            _hint.Text = "结束日期不能早于起始日期";
            _ok.Enabled = false;
        }
        else if (t.DayNumber - f.DayNumber + 1 > 366)
        {
            _hint.Text = $"区间最多 366 天（当前 {t.DayNumber - f.DayNumber + 1} 天）";
            _ok.Enabled = false;
        }
        else
        {
            _hint.Text = $"共 {t.DayNumber - f.DayNumber + 1} 天";
            _ok.Enabled = true;
        }
        return _ok.Enabled;
    }

    public DateOnly FromDate => DateOnly.FromDateTime(_from.Value);
    public DateOnly ToDate => DateOnly.FromDateTime(_to.Value);
}

/// <summary>通用二次确认（清空数据、从日志重建等危险操作，§6.7 / §7.4）。</summary>
internal sealed class ConfirmDialog : Form
{
    private readonly TextBox _input = new();

    private ConfirmDialog(string title, string message, bool requireWord, string word)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);
        ClientSize = new Size(460, requireWord ? 214 : 172);

        var msg = new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 0),
            ForeColor = Theme.Current.Fore
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        body.Controls.Add(msg);

        if (requireWord)
        {
            _input.Dock = DockStyle.Bottom;
            _input.Height = 26;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.BackColor = Theme.Current.SurfaceAlt;
            _input.ForeColor = Theme.Current.Fore;
            _input.Font = Theme.Font(10f);
            _input.TextChanged += (_, _) =>
            {
                // 必须逐字输入 DELETE（§6.7 二次确认）
                _ok.Enabled = string.Equals(_input.Text.Trim(), word, StringComparison.Ordinal);
            };
            var wrap = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(16, 6, 16, 12), BackColor = Color.Transparent };
            wrap.Controls.Add(_input);
            var hint = new Label
            {
                Text = $"确认请输入 {word}",
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = Theme.Current.Muted
            };
            wrap.Controls.Add(hint);
            wrap.Controls.SetChildIndex(hint, 0);
            body.Controls.Add(wrap);
        }

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(0, 8, 14, 0),
            BackColor = Color.Transparent
        };
        _ok.Text = "确认";
        _ok.Width = 92;
        _ok.Height = 30;
        _ok.Primary = true;
        _ok.DialogResult = DialogResult.OK;
        _ok.Enabled = !requireWord;
        var cancel = new FlatButton { Text = "取消", Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
        bar.Controls.Add(_ok);
        bar.Controls.Add(cancel);

        Controls.Add(body);
        Controls.Add(bar);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private readonly FlatButton _ok = new();

    public static bool Ask(IWin32Window owner, string title, string message, bool requireWord = true)
    {
        try
        {
            using var dlg = new ConfirmDialog(title, message, requireWord, "DELETE");
            return dlg.ShowDialog(owner) == DialogResult.OK;
        }
        catch (Exception ex)
        {
            Log.Error("确认框显示失败，按取消处理（红线：不静默执行危险操作）", ex);
            return false;
        }
    }
}

/// <summary>输入一行文本（设置页改数据目录等）。</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _box = new();

    private InputDialog(string title, string label, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);
        ClientSize = new Size(440, 150);

        var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 30, Padding = new Padding(14, 12, 14, 0) };
        _box.Dock = DockStyle.Top;
        _box.Text = initial;
        _box.Font = Theme.Font(10f);
        _box.BackColor = Theme.Current.SurfaceAlt;
        _box.ForeColor = Theme.Current.Fore;
        _box.BorderStyle = BorderStyle.FixedSingle;
        _box.Margin = new Padding(14);

        var pad = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(14, 4, 14, 6), BackColor = Color.Transparent };
        pad.Controls.Add(_box);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(0, 6, 14, 0),
            BackColor = Color.Transparent
        };
        var ok = new FlatButton { Text = "确定", Width = 86, Height = 30, Primary = true, DialogResult = DialogResult.OK };
        var cancel = new FlatButton { Text = "取消", Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
        bar.Controls.Add(ok);
        bar.Controls.Add(cancel);

        Controls.Add(pad);
        Controls.Add(lbl);
        Controls.Add(bar);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static string? Ask(IWin32Window owner, string title, string label, string initial)
    {
        try
        {
            using var dlg = new InputDialog(title, label, initial);
            return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Value : null;
        }
        catch (Exception ex)
        {
            Log.Error("输入框显示失败", ex);
            return null;
        }
    }

    public string Value => _box.Text.Trim();
}
