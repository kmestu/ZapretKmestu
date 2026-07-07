#nullable disable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZapretKmestu.UI;

public class DarkTrayMenuColorTable : ProfessionalColorTable
{
    // Background colors
    public override Color ToolStripDropDownBackground => Color.FromArgb(30, 30, 30);
    public override Color MenuBorder => Color.FromArgb(50, 50, 50);
    public override Color MenuItemBorder => Color.FromArgb(50, 50, 50);
    
    // Gradient margins
    public override Color ImageMarginGradientBegin => Color.FromArgb(30, 30, 30);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 30, 30);
    public override Color ImageMarginGradientEnd => Color.FromArgb(30, 30, 30);

    // Hover / Selected colors
    public override Color MenuItemSelected => Color.FromArgb(0, 70, 130);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0, 70, 130);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0, 70, 130);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(0, 60, 120);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(0, 60, 120);

    // Separator colors
    public override Color SeparatorDark => Color.FromArgb(60, 60, 60);
    public override Color SeparatorLight => Color.FromArgb(30, 30, 30);

    // Check box background (if drawn normally)
    public override Color CheckBackground => Color.FromArgb(70, 70, 70);
    public override Color CheckSelectedBackground => Color.FromArgb(80, 80, 80);
    public override Color CheckPressedBackground => Color.FromArgb(60, 60, 60);
}

public class DarkTrayMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkTrayMenuRenderer() : base(new DarkTrayMenuColorTable())
    {
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.Enabled)
        {
            e.TextColor = Color.FromArgb(240, 240, 240);
        }
        else
        {
            e.TextColor = Color.FromArgb(120, 120, 120);
        }
        base.OnRenderItemText(e);
    }
    
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed))
        {
            var rect = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            using var brush = new SolidBrush(e.Item.Pressed ? ColorTable.MenuItemPressedGradientBegin : ColorTable.MenuItemSelected);
            e.Graphics.FillRectangle(brush, rect);
            
            using var pen = new Pen(Color.FromArgb(30, 90, 150));
            e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.Item.Width - 1, e.Item.Height - 1));
        }
        else
        {
            base.OnRenderMenuItemBackground(e);
        }
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item.Enabled ? Color.FromArgb(240, 240, 240) : Color.FromArgb(120, 120, 120);
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Draw checkmark background using color table
        var bounds = new Rectangle(2, 1, e.Item.Height - 2, e.Item.Height - 2);
        bool isSelected = e.Item.Selected;
        
        using (var bgBrush = new SolidBrush(isSelected ? ColorTable.CheckSelectedBackground : ColorTable.CheckBackground))
        {
            e.Graphics.FillRectangle(bgBrush, bounds);
        }
        
        using (var borderPen = new Pen(ColorTable.MenuItemBorder))
        {
            e.Graphics.DrawRectangle(borderPen, bounds);
        }

        // Draw a light colored custom checkmark
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = e.ImageRectangle;
        
        using var pen = new Pen(Color.FromArgb(240, 240, 240), 2);
        Point[] points = {
            new Point(rect.Left + 3, rect.Top + rect.Height / 2),
            new Point(rect.Left + rect.Width / 2 - 1, rect.Bottom - 4),
            new Point(rect.Right - 3, rect.Top + 3)
        };
        e.Graphics.DrawLines(pen, points);
    }
}
