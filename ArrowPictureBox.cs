using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace _125GateApp
{
    public enum ArrowDirection
    {
        Right,
        Left,
        Up,
        Down
    }

    /// <summary>
    /// 箭头形状的 PictureBox：形状由 GDI+ 矢量绘制，不需要导入图片，可通过 ArrowColor 变色。
    /// </summary>
    [ToolboxItem(true)]
    [Description("可变色的箭头形状控件")]
    public class ArrowPictureBox : PictureBox
    {
        private Color arrowColor = Color.Lime;
        private ArrowDirection direction = ArrowDirection.Right;

        [Category("外观"), DefaultValue(typeof(Color), "Lime"), Description("箭头的填充颜色")]
        public Color ArrowColor
        {
            get { return arrowColor; }
            set { arrowColor = value; Invalidate(); }
        }

        [Category("外观"), DefaultValue(ArrowDirection.Right), Description("箭头的指向")]
        public ArrowDirection Direction
        {
            get { return direction; }
            set { direction = value; Invalidate(); }
        }

        public ArrowPictureBox()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            // 不调用 base.OnPaint：父类会绘制 Image，这里完全改为绘制箭头
            Graphics g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float w = Width - 2f;
            float h = Height - 2f;
            if (w <= 2f || h <= 2f)
            {
                return;
            }

            float shaftTop = h * 0.32f;
            float shaftBottom = h * 0.68f;
            float headLeft = w * 0.55f;

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddPolygon(new[]
                {
                    new PointF(1f, 1f + shaftTop),
                    new PointF(1f + headLeft, 1f + shaftTop),
                    new PointF(1f + headLeft, 1f),
                    new PointF(1f + w, 1f + h / 2f),
                    new PointF(1f + headLeft, 1f + h),
                    new PointF(1f + headLeft, 1f + shaftBottom),
                    new PointF(1f, 1f + shaftBottom)
                });

                float angle = 0f;
                if (Direction == ArrowDirection.Down)
                {
                    angle = 90f;
                }
                else if (Direction == ArrowDirection.Left)
                {
                    angle = 180f;
                }
                else if (Direction == ArrowDirection.Up)
                {
                    angle = 270f;
                }

                if (angle != 0f)
                {
                    using (Matrix matrix = new Matrix())
                    {
                        matrix.RotateAt(angle, new PointF(1f + w / 2f, 1f + h / 2f));
                        path.Transform(matrix);
                    }
                }

                using (SolidBrush brush = new SolidBrush(ArrowColor))
                {
                    g.FillPath(brush, path);
                }
            }
        }
    }
}
