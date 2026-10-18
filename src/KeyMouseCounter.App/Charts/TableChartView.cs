using System.Drawing;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Charts;

/// <summary>明细表格（§7.2.3）：只读、可排序、不自动抢占焦点。</summary>
public sealed class TableChartView : IChartView
{
    private static Color Surface => Theme.Current.ChartBack;
    private static Color Grid => Theme.Current.Grid;
    private static Color Fore => Theme.Current.ChartFore;
    private static Color Head => Theme.Current.Head;
    private static Color Muted => Theme.Current.Muted;

    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Surface };
    private TableChartData? _data;

    public TableChartView()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.BackgroundColor = Surface;
        _grid.GridColor = Grid;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Head;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Head;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 30;
        _grid.DefaultCellStyle.BackColor = Surface;
        _grid.DefaultCellStyle.ForeColor = Fore;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x1E, 0x3A, 0x8A);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f);
        _grid.RowTemplate.Height = 24;
        _grid.ScrollBars = ScrollBars.Vertical;
        _grid.ColumnHeaderMouseClick += OnHeaderClick;

        _host.Controls.Add(_grid);
    }

    /// <summary>主题换色时重新套一遍配色（表格配色是控件属性，不能像绘图那样每帧现取）。</summary>
    public void ApplyTheme()
    {
        var p = Theme.Current;
        _host.BackColor = p.ChartBack;
        _grid.BackgroundColor = p.ChartBack;
        _grid.GridColor = p.Grid;
        _grid.ForeColor = p.Fore;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = p.Head;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Muted;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Head;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Fore;
        _grid.DefaultCellStyle.BackColor = p.ChartBack;
        _grid.DefaultCellStyle.ForeColor = p.Fore;
        _grid.DefaultCellStyle.SelectionBackColor = p.AccentSoft;
        _grid.DefaultCellStyle.SelectionForeColor = p.Fore;
    }

    public Control Host => _host;

    public void Render(ChartData data)
    {
        if (data is not TableChartData d)
            throw new ArgumentException($"TableChartView 需要 TableChartData，收到 {data.GetType().Name}", nameof(data));

        _data = d;
        ApplyTheme();
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            _grid.Columns.Clear();

            for (int i = 0; i < d.Headers.Length; i++)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = d.Headers[i],
                    HeaderText = d.Headers[i],
                    SortMode = DataGridViewColumnSortMode.Automatic,
                    FillWeight = d.ColumnWidths is { Length: > 0 } w && i < w.Length ? w[i] : 100
                });

            foreach (var row in d.Rows)
            {
                int idx = _grid.Rows.Add();
                var cells = _grid.Rows[idx].Cells;
                for (int c = 0; c < d.Headers.Length; c++)
                {
                    object? raw = c < row.Length ? row[c] : null;
                    cells[c].Value = d.Format?.Invoke(c, raw) ?? raw?.ToString() ?? "";
                    cells[c].Tag = raw;
                }
            }

            // 空数据时也保留表头，并在表体里放一行提示
            if (d.Rows.Count == 0)
            {
                int idx = _grid.Rows.Add();
                var cells = _grid.Rows[idx].Cells;
                cells[0].Value = d.EmptyText;
                cells[0].Style.ForeColor = Muted;
                _grid.Rows[idx].ReadOnly = true;
                if (_grid.Columns.Count > 1) _grid.Rows[idx].Cells[1].Style.ForeColor = Muted;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"表格渲染失败：{ex.Message}");
        }
        finally
        {
            _grid.ResumeLayout();
        }
    }

    /// <summary>点表头排序：按 Tag 里的原始值排，不按显示文本排（"1.2w" 排不动）。</summary>
    private void OnHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_data is null || e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
        var col = _grid.Columns[e.ColumnIndex];

        bool asc = col.Tag is not "desc";
        foreach (DataGridViewColumn c in _grid.Columns) c.Tag = null;
        col.Tag = asc ? "desc" : "asc";

        try
        {
            _grid.Sort(col, asc ? System.ComponentModel.ListSortDirection.Ascending
                                : System.ComponentModel.ListSortDirection.Descending);
            col.HeaderCell.SortGlyphDirection = asc ? SortOrder.Ascending : SortOrder.Descending;
        }
        catch (Exception ex)
        {
            // 空表占位行会让排序抛异常，忽略即可
            Log.Debug($"表格排序失败：{ex.Message}");
        }
    }
}
