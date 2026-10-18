using System.Drawing;
using KeyMouseCounter.Diagnostics;
using KeyMouseCounter.Export;
using KeyMouseCounter.Services;

namespace KeyMouseCounter.UI;

/// <summary>导出 CSV（§6.9 / §7.4 数据页）：三档粒度，UTF-8 with BOM。</summary>
internal sealed class ExportDialog : Form
{
    private readonly StatsQuery _query;
    private readonly RadioButton _byDay = new() { Text = "按天（日期范围）", Checked = true };
    private readonly RadioButton _byHour = new() { Text = "按小时（单日 0-23 时）" };
    private readonly RadioButton _byMinute = new() { Text = "按分钟（单日 0-1439 分，默认只导非零）" };
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly Label _fromLabel = new() { Text = "起始日期" };
    private readonly Label _toLabel = new() { Text = "结束日期" };
    private readonly CheckBox _includeZero = new() { Text = "包含全零分钟（1440 行）" };
    private readonly CheckBox _openAfter = new() { Text = "导出后打开所在文件夹", Checked = true };
    private readonly Label _hint = new();

    public ExportDialog(StatsQuery query, DateOnly defaultDay)
    {
        _query = query;

        Text = "导出 CSV";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(468, 336);
        BackColor = Theme.Current.Window;
        ForeColor = Theme.Current.Fore;
        Font = Theme.Font(9f);

        var today = DateOnly.FromDateTime(DateTime.Today);
        _from.Value = defaultDay.ToDateTime(TimeOnly.MinValue);
        _to.Value = today.ToDateTime(TimeOnly.MinValue);
        _from.MaxDate = today.ToDateTime(TimeOnly.MinValue);
        _to.MaxDate = today.ToDateTime(TimeOnly.MinValue);
        _to.MinDate = _from.MinDate;

        int y = 16;
        foreach (var rb in new[] { _byDay, _byHour, _byMinute })
        {
            rb.Location = new Point(18, y);
            rb.AutoSize = true;
            rb.ForeColor = Theme.Current.Fore;
            rb.CheckedChanged += (_, _) => SyncMode();
            Controls.Add(rb);
            y += 30;
        }

        _fromLabel.Location = new Point(40, y + 4);
        _fromLabel.AutoSize = true;
        _fromLabel.ForeColor = Theme.Current.Muted;
        Controls.Add(_fromLabel);

        _from.Location = new Point(104, y);
        _from.Width = 130;
        _from.Format = DateTimePickerFormat.Custom;
        _from.CustomFormat = "yyyy-MM-dd";
        _from.ValueChanged += (_, _) => { if (_to.Value < _from.Value) _to.Value = _from.Value; SyncMode(); };
        Controls.Add(_from);

        _toLabel.Location = new Point(248, y + 4);
        _toLabel.AutoSize = true;
        _toLabel.ForeColor = Theme.Current.Muted;
        Controls.Add(_toLabel);

        _to.Location = new Point(312, y);
        _to.Width = 130;
        _to.Format = DateTimePickerFormat.Custom;
        _to.CustomFormat = "yyyy-MM-dd";
        Controls.Add(_to);
        y += 38;

        _includeZero.Location = new Point(40, y);
        _includeZero.AutoSize = true;
        _includeZero.ForeColor = Theme.Current.Fore;
        Controls.Add(_includeZero);
        y += 32;

        _openAfter.Location = new Point(40, y);
        _openAfter.AutoSize = true;
        _openAfter.ForeColor = Theme.Current.Fore;
        Controls.Add(_openAfter);
        y += 30;

        _hint.Location = new Point(40, y);
        _hint.Size = new Size(400, 20);
        _hint.ForeColor = Theme.Current.Muted;
        Controls.Add(_hint);

        var ok = new FlatButton { Text = "导出…", Width = 96, Height = 30, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => DoExport();
        var cancel = new FlatButton { Text = "取消", Width = 76, Height = 30, DialogResult = DialogResult.Cancel };
        Controls.Add(ok);
        Controls.Add(cancel);
        ok.Location = new Point(ClientSize.Width - 96 - 18, ClientSize.Height - 46);
        cancel.Location = new Point(ClientSize.Width - 96 - 18 - 76 - 8, ClientSize.Height - 46);
        AcceptButton = ok;
        CancelButton = cancel;

        SyncMode();
    }

    private ExportKind Kind => _byDay.Checked ? ExportKind.Day : _byHour.Checked ? ExportKind.Hour : ExportKind.Minute;

    private void SyncMode()
    {
        bool multi = Kind == ExportKind.Day;
        _from.Enabled = multi;
        _fromLabel.Enabled = multi;
        _to.Visible = multi;
        _toLabel.Visible = multi;
        _includeZero.Visible = Kind == ExportKind.Minute;
        _hint.Text = Kind switch
        {
            ExportKind.Day => "列：日期,键盘按键,鼠标左键,鼠标右键,鼠标中键,滚轮上,滚轮下,活跃时长(秒),首次输入,最后输入",
            ExportKind.Hour => "列：日期,小时,键盘按键,鼠标点击,滚轮,活跃时长(秒)",
            _ => "列：日期,时间(HH:mm),键盘按键,鼠标点击,滚轮,活跃时长(秒)"
        };
    }

    private void DoExport()
    {
        try
        {
            var day = DateOnly.FromDateTime(_from.Value);
            var to = DateOnly.FromDateTime(_to.Value);
            if (Kind != ExportKind.Day && to != day) to = day;

            string suggested = CsvExporter.SuggestFileName(Kind switch
            {
                ExportKind.Day => "days",
                ExportKind.Hour => "hours",
                _ => "minutes"
            }, day, Kind == ExportKind.Day ? to : null);

            using var sfd = new SaveFileDialog
            {
                Title = "导出 CSV",
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = suggested,
                InitialDirectory = Directory.Exists(AppPaths.ExportDir) ? AppPaths.ExportDir : AppPaths.Root,
                OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            AppPaths.EnsureCreated();
            switch (Kind)
            {
                case ExportKind.Day:
                    CsvExporter.ExportDays(sfd.FileName, _query.GetDayRows(day, to));
                    break;
                case ExportKind.Hour:
                    CsvExporter.ExportHours(sfd.FileName, day, _query.GetHours(day));
                    break;
                default:
                    CsvExporter.ExportMinutes(sfd.FileName, day, _query.GetMinutes(day), _includeZero.Checked);
                    break;
            }

            Log.Info($"已导出 CSV：{Path.GetFileName(sfd.FileName)}");
            if (_openAfter.Checked)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", $"/select,\"{sfd.FileName}\"") { UseShellExecute = true });
                }
                catch { }
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("导出 CSV 失败", ex);
            MessageBox.Show(this, "导出失败：\r\n" + ex.Message, "KeyMouseCounter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal enum ExportKind { Day, Hour, Minute }
