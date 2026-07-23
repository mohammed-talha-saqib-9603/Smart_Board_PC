// ==============================================================================
// SMARTBOARD PC - C# NATIVE AOT VERSION
// ==============================================================================
// This is a complete rewrite of the Smartboard PC app in C# with Native AOT
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartboardPC
{
    // ==========================================================================
    // PROGRAM ENTRY POINT
    // ==========================================================================
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainWindow());
        }
    }

    // ==========================================================================
    // MAIN WINDOW
    // ==========================================================================
    public class MainWindow : Form
    {
        private SmartboardCanvas canvas;
        private FloatingToolbar toolbar;
        private NetworkManager network;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;

        public MainWindow()
        {
            Text = "Smartboard PC - C# Native AOT";
            Size = new Size(1440, 900);
            BackColor = Color.FromArgb(0x0B, 0x0C, 0x0E);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;

            // Initialize network
            network = new NetworkManager();
            network.PhoneConnected += OnPhoneConnected;
            network.DrawReceived += OnDrawReceived;
            network.EraseReceived += OnEraseReceived;
            network.ScrollReceived += OnScrollReceived;
            network.ZoomReceived += OnZoomReceived;
            network.ToolReceived += OnToolReceived;
            network.StrokeWidthReceived += OnStrokeWidthReceived;
            network.HelloReceived += OnHelloReceived;

            // Initialize canvas
            canvas = new SmartboardCanvas(network);
            canvas.Dock = DockStyle.Fill;
            canvas.BackColor = Color.FromArgb(0x0B, 0x0C, 0x0E);

            // Initialize toolbar
            toolbar = new FloatingToolbar(canvas);
            toolbar.Parent = canvas;

            // Initialize status strip
            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("Disconnected");
            statusLabel.ForeColor = Color.Red;
            statusStrip.Items.Add(statusLabel);
            statusStrip.BackColor = Color.FromArgb(28, 30, 34);

            // Layout
            Controls.Add(canvas);
            Controls.Add(statusStrip);
            statusStrip.BringToFront();

            // Start network
            network.Start();

            // Resize handler
            Resize += OnResize;

            // Keyboard shortcuts
            KeyPreview = true;
            KeyDown += OnKeyDown;
        }

        private void OnResize(object sender, EventArgs e)
        {
            toolbar.UpdatePosition();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Z)
            {
                canvas.Undo();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Y)
            {
                canvas.Redo();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (WindowState == FormWindowState.Normal)
                {
                    canvas.ClearCurrentPage();
                }
                else
                {
                    WindowState = FormWindowState.Normal;
                }
                e.Handled = true;
            }
        }

        private void ToggleFullscreen()
        {
            if (WindowState == FormWindowState.Normal)
            {
                WindowState = FormWindowState.Maximized;
                FormBorderStyle = FormBorderStyle.None;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
            }
        }

        private void OnPhoneConnected(string ip)
        {
            statusLabel.Text = $"Connected: {ip}";
            statusLabel.ForeColor = Color.FromArgb(0x57, 0xF2, 0x87); // Green
        }

        private void OnDrawReceived(float x, float y, string state, string tool)
        {
            canvas.OnDrawPacket(x, y, state, tool);
        }

        private void OnEraseReceived(string mode, string state, float x, float y)
        {
            canvas.OnErasePacket(mode, state, x, y);
        }

        private void OnScrollReceived(float dx, float dy)
        {
            canvas.OnScrollPacket(dx, dy);
        }

        private void OnZoomReceived(float factor)
        {
            canvas.ZoomViewport(factor);
        }

        private void OnToolReceived(string tool)
        {
            canvas.OnToolPacket(tool);
        }

        private void OnStrokeWidthReceived(int width)
        {
            canvas.CurrentWidth = width;
        }

        private void OnHelloReceived()
        {
            canvas.PushFullStateToPhone();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            network.Stop();
            base.OnFormClosing(e);
        }
    }

    // ==========================================================================
    // SMARTBOARD CANVAS
    // ==========================================================================
    public class SmartboardCanvas : UserControl
    {
        private const int CANVAS_W = 3840;
        private const int CANVAS_H = 2160;
        private const float MIN_VP_W = 220f;
        private const float MAX_VP_W = CANVAS_W;
        private const float LASER_FADE_SECONDS = 2.5f;
        private const float PAN_SENSITIVITY = 3.0f;

        private NetworkManager network;
        private Dictionary<int, Page> pages = new Dictionary<int, Page>();
        private int currentPage = 1;

        private Color bgColor = Color.Black;
        private bool gridEnabled = false;

        private RectangleF viewportRect;
        private string currentTool = "pen";
        private Color currentColor = Color.White;
        private float currentWidth = 4.0f;
        private float eraserRadius = 35.0f;
        private string eraserSizeName = "Medium";

        private Stroke activeStroke;
        private Dictionary<string, Stroke> networkStrokes = new();
        private List<Stroke> fadingStrokes = new();

        private PointF? eraseBoxStart = null;
        private RectangleF? activeEraseBox = null;
        private PointF? shapeStart = null;
        private ShapeItem activeShape = null;

        private float dashPhase = 0f;
        private Timer fadeTimer;
        private Timer dashTimer;

        private PointF? mouseDownPos = null;
        private bool isDragging = false;
        private PointF dragStart = PointF.Empty;

        public float CurrentWidth { get => currentWidth; set => currentWidth = value; }

        public SmartboardCanvas(NetworkManager network)
        {
            this.network = network;
            this.network = network;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer | ControlStyles.ResizeRedraw, true);

            pages[currentPage] = new Page();

            viewportRect = new RectangleF(
                (CANVAS_W - 1280) / 2.0f,
                (CANVAS_H - 720) / 2.0f,
                1280f,
                720f
            );

            // Setup timers
            fadeTimer = new Timer();
            fadeTimer.Interval = 50;
            fadeTimer.Tick += (s, e) => TickFade();
            fadeTimer.Start();

            dashTimer = new Timer();
            dashTimer.Interval = 120;
            dashTimer.Tick += (s, e) => { dashPhase = (dashPhase + 2) % 32; Invalidate(); };
            dashTimer.Start();

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            MouseWheel += OnMouseWheel;

            BackColor = Color.FromArgb(0x0B, 0x0C, 0x0E);
        }

        // ======================================================================
        // COORDINATE TRANSFORMS
        // ======================================================================
        private float FitScale()
        {
            if (Width <= 0 || Height <= 0) return 0.0001f;
            return Math.Min(Width / (float)CANVAS_W, Height / (float)CANVAS_H);
        }

        private PointF OriginOffset()
        {
            float s = FitScale();
            float ox = (Width - CANVAS_W * s) / 2.0f;
            float oy = (Height - CANVAS_H * s) / 2.0f;
            return new PointF(ox, oy);
        }

        private PointF ScreenToCanvas(PointF pt)
        {
            float s = FitScale();
            PointF off = OriginOffset();
            return new PointF((pt.X - off.X) / s, (pt.Y - off.Y) / s);
        }

        private PointF CanvasToScreen(PointF pt)
        {
            float s = FitScale();
            PointF off = OriginOffset();
            return new PointF(pt.X * s + off.X, pt.Y * s + off.Y);
        }

        private RectangleF CanvasRectToScreen(RectangleF r)
        {
            PointF tl = CanvasToScreen(new PointF(r.X, r.Y));
            float s = FitScale();
            return new RectangleF(tl.X, tl.Y, r.Width * s, r.Height * s);
        }

        private PointF PhoneNormToCanvas(float nx, float ny)
        {
            return new PointF(
                viewportRect.X + nx * viewportRect.Width,
                viewportRect.Y + ny * viewportRect.Height
            );
        }

        // ======================================================================
        // NETWORK PACKET HANDLERS
        // ======================================================================
        public void OnDrawPacket(float x, float y, string state, string tool)
        {
            PointF pt = PhoneNormToCanvas(x, y);

            if (state == "down")
            {
                Stroke stroke = new Stroke
                {
                    Id = Guid.NewGuid().ToString(),
                    Tool = tool,
                    Color = Color.FromArgb(currentColor.ToArgb()),
                    BaseWidth = currentWidth
                };
                stroke.Points.Add(new StrokePoint { X = pt.X, Y = pt.Y, T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f });
                networkStrokes[stroke.Id] = stroke;
                activeStroke = stroke;
            }
            else if (state == "move" && activeStroke != null)
            {
                activeStroke.Points.Add(new StrokePoint { X = pt.X, Y = pt.Y, T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f });
                Invalidate();
            }
            else if (state == "up" && activeStroke != null)
            {
                activeStroke.Points.Add(new StrokePoint { X = pt.X, Y = pt.Y, T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f });
                FinalizeStroke(activeStroke);
                if (networkStrokes.ContainsKey(activeStroke.Id))
                    networkStrokes.Remove(activeStroke.Id);
                activeStroke = null;
                Invalidate();
            }
        }

        public void OnErasePacket(string mode, string state, float x, float y)
        {
            PointF pt = PhoneNormToCanvas(x, y);

            if (mode == "area")
            {
                if (state == "down")
                {
                    eraseBoxStart = pt;
                    activeEraseBox = new RectangleF(pt, new SizeF(0, 0));
                }
                else if (state == "move" && eraseBoxStart.HasValue)
                {
                    activeEraseBox = CreateRect(eraseBoxStart.Value, pt);
                }
                else if (state == "up" && activeEraseBox.HasValue)
                {
                    EraseArea(activeEraseBox.Value);
                    activeEraseBox = null;
                    eraseBoxStart = null;
                }
            }
            else // stroke mode
            {
                if (state == "down" || state == "move")
                {
                    EraseStrokeNear(pt);
                }
            }
            Invalidate();
        }

        public void OnScrollPacket(float dx, float dy)
        {
            float scrollSensitivity = 800f;
            PanViewport(dx * scrollSensitivity, dy * scrollSensitivity);
        }

        public void ZoomViewport(float factor)
        {
            float cx = viewportRect.X + viewportRect.Width / 2;
            float cy = viewportRect.Y + viewportRect.Height / 2;
            float aspect = viewportRect.Height / viewportRect.Width;

            float newW = Math.Clamp(viewportRect.Width * factor, MIN_VP_W, MAX_VP_W);
            float newH = Math.Clamp(newW * aspect, MIN_VP_W * aspect, CANVAS_H);

            float nx = Math.Clamp(cx - newW / 2, 0, CANVAS_W - newW);
            float ny = Math.Clamp(cy - newH / 2, 0, CANVAS_H - newH);

            viewportRect = new RectangleF(nx, ny, newW, newH);
            NotifyViewportChange();
        }

        public void OnToolPacket(string tool)
        {
            switch (tool)
            {
                case "clear": ClearCurrentPage(); break;
                case "undo": Undo(); break;
                case "redo": Redo(); break;
                case "new_page": AddPage(); break;
                default:
                    if (new[] { "pen", "paint_brush", "fountain_pen", "laser", "eraser_stroke", "eraser_area" }.Contains(tool))
                        SetTool(tool);
                    break;
            }
        }

        public void PushFullStateToPhone()
        {
            network.SendPage(currentPage);
            network.SendToolChange(currentTool);
            network.SendStrokeWidth((int)currentWidth);
            network.SendViewport(viewportRect.X, viewportRect.Y, viewportRect.Width, viewportRect.Height, true);
        }

        // ======================================================================
        // STROKE LIFECYCLE
        // ======================================================================
        private void FinalizeStroke(Stroke stroke)
        {
            if (stroke.Points.Count < 2) return;

            if (!pages.ContainsKey(currentPage))
                pages[currentPage] = new Page();

            Page page = pages[currentPage];
            page.Strokes.Add(stroke);

            if (stroke.Tool == "laser")
            {
                stroke.FadeStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f;
                fadingStrokes.Add(stroke);
            }

            // Add to undo stack
            page.PushCommand(new Command(
                undo: () => { if (page.Strokes.Contains(stroke)) page.Strokes.Remove(stroke); Invalidate(); },
                redo: () => { page.Strokes.Add(stroke); Invalidate(); }
            ));

            Invalidate();
        }

        private void TickFade()
        {
            if (fadingStrokes.Count == 0) return;

            float now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f;
            Page page = pages.TryGetValue(currentPage, out var p) ? p : null;

            List<Stroke> stillFading = new();
            bool changed = false;

            foreach (var s in fadingStrokes)
            {
                if (!s.FadeStart.HasValue) continue;

                float elapsed = now - s.FadeStart.Value;
                if (elapsed >= LASER_FADE_SECONDS)
                {
                    if (page != null && page.Strokes.Contains(s))
                        page.Strokes.Remove(s);
                    changed = true;
                }
                else
                {
                    s.Opacity = Math.Max(0, 1 - elapsed / LASER_FADE_SECONDS);
                    stillFading.Add(s);
                    changed = true;
                }
            }

            fadingStrokes = stillFading;
            if (changed) Invalidate();
        }

        // ======================================================================
        // ERASE FUNCTIONS
        // ======================================================================
        private void EraseStrokeNear(PointF canvasPt)
        {
            float radiusSq = eraserRadius * eraserRadius;
            bool changed = ApplyEraseMask((p) =>
            {
                float dx = p.X - canvasPt.X;
                float dy = p.Y - canvasPt.Y;
                return dx * dx + dy * dy <= radiusSq;
            });
            if (changed) Invalidate();
        }

        private void EraseArea(RectangleF rect)
        {
            bool changed = ApplyEraseMask((p) => rect.Contains(p.X, p.Y));
            if (changed) Invalidate();
        }

        private bool ApplyEraseMask(Func<StrokePoint, bool> shouldErase)
        {
            if (!pages.TryGetValue(currentPage, out Page page)) return false;

            List<Stroke> removedOriginals = new();
            List<Stroke> addedSegments = new();
            List<Stroke> untouched = new();
            bool changed = false;

            foreach (var stroke in page.Strokes)
            {
                if (stroke.Tool == "laser")
                {
                    untouched.Add(stroke);
                    continue;
                }

                var keepMask = stroke.Points.Select(p => !shouldErase(p)).ToList();
                if (keepMask.All(k => k))
                {
                    untouched.Add(stroke);
                    continue;
                }

                changed = true;
                removedOriginals.Add(stroke);
                addedSegments.AddRange(SplitStrokeByKeepMask(stroke, keepMask));
            }

            if (!changed) return false;

            page.Strokes = untouched.Concat(addedSegments).ToList();

            // Add to undo stack
            var origCopy = new List<Stroke>(removedOriginals);
            var addedCopy = new List<Stroke>(addedSegments);
            page.PushCommand(new Command(
                undo: () => { foreach (var s in addedCopy) if (page.Strokes.Contains(s)) page.Strokes.Remove(s); page.Strokes.AddRange(origCopy); Invalidate(); },
                redo: () => { foreach (var s in origCopy) if (page.Strokes.Contains(s)) page.Strokes.Remove(s); page.Strokes.AddRange(addedCopy); Invalidate(); }
            ));

            return true;
        }

        private List<Stroke> SplitStrokeByKeepMask(Stroke stroke, List<bool> keepMask)
        {
            List<Stroke> result = new();
            List<StrokePoint> current = new();

            for (int i = 0; i < stroke.Points.Count; i++)
            {
                if (keepMask[i])
                {
                    current.Add(stroke.Points[i]);
                }
                else
                {
                    if (current.Count >= 2)
                        result.Add(new Stroke
                        {
                            Id = Guid.NewGuid().ToString(),
                            Tool = stroke.Tool,
                            Color = Color.FromArgb(stroke.Color.ToArgb()),
                            BaseWidth = stroke.BaseWidth,
                            Points = new List<StrokePoint>(current),
                            Created = stroke.Created,
                            Layer = stroke.Layer
                        });
                    current = new();
                }
            }

            if (current.Count >= 2)
                result.Add(new Stroke
                {
                    Id = Guid.NewGuid().ToString(),
                    Tool = stroke.Tool,
                    Color = Color.FromArgb(stroke.Color.ToArgb()),
                    BaseWidth = stroke.BaseWidth,
                    Points = new List<StrokePoint>(current),
                    Created = stroke.Created,
                    Layer = stroke.Layer
                });

            return result;
        }

        // ======================================================================
        // VIEWPORT CONTROLS
        // ======================================================================
        private void PanViewport(float dx, float dy)
        {
            float nx = Math.Clamp(viewportRect.X + dx, 0, CANVAS_W - viewportRect.Width);
            float ny = Math.Clamp(viewportRect.Y + dy, 0, CANVAS_H - viewportRect.Height);
            viewportRect = new RectangleF(nx, ny, viewportRect.Width, viewportRect.Height);
            NotifyViewportChange();
        }

        private void NotifyViewportChange()
        {
            network.SendViewport(viewportRect.X, viewportRect.Y, viewportRect.Width, viewportRect.Height);
            Invalidate();
        }

        // ======================================================================
        // TOOL / PAGE MANAGEMENT
        // ======================================================================
        public void SetTool(string tool)
        {
            currentTool = tool;
            network.SendToolChange(tool);
        }

        public void Undo()
        {
            if (pages.TryGetValue(currentPage, out Page page))
            {
                page.Undo();
                Invalidate();
            }
        }

        public void Redo()
        {
            if (pages.TryGetValue(currentPage, out Page page))
            {
                page.Redo();
                Invalidate();
            }
        }

        public void ClearCurrentPage()
        {
            if (!pages.TryGetValue(currentPage, out Page page)) return;

            var oldStrokes = new List<Stroke>(page.Strokes);
            var oldShapes = new List<ShapeItem>(page.Shapes);
            var oldImages = new List<ImageItem>(page.Images);
            var oldNotes = new List<StickyNoteData>(page.Notes);

            page.Strokes.Clear();
            page.Shapes.Clear();
            page.Images.Clear();
            page.Notes.Clear();

            page.PushCommand(new Command(
                undo: () => { page.Strokes = oldStrokes; page.Shapes = oldShapes; page.Images = oldImages; page.Notes = oldNotes; Invalidate(); },
                redo: () => { page.Strokes.Clear(); page.Shapes.Clear(); page.Images.Clear(); page.Notes.Clear(); Invalidate(); }
            ));

            Invalidate();
        }

        private void AddPage()
        {
            int newNum = pages.Keys.Max() + 1;
            pages[newNum] = new Page();
            currentPage = newNum;
            network.SendPage(currentPage);
            Invalidate();
        }

        // ======================================================================
        // MOUSE HANDLERS
        // ======================================================================
        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            PointF pt = ScreenToCanvas(new PointF(e.X, e.Y));

            if (new[] { "pen", "fountain_pen", "paint_brush", "laser" }.Contains(currentTool))
            {
                activeStroke = new Stroke
                {
                    Id = Guid.NewGuid().ToString(),
                    Tool = currentTool,
                    Color = Color.FromArgb(currentColor.ToArgb()),
                    BaseWidth = currentWidth
                };
                activeStroke.Points.Add(new StrokePoint { X = pt.X, Y = pt.Y, T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f });
            }
            else if (new[] { "rectangle", "circle", "arrow", "line" }.Contains(currentTool))
            {
                shapeStart = pt;
                activeShape = new ShapeItem
                {
                    Id = Guid.NewGuid().ToString(),
                    ShapeType = currentTool,
                    Color = Color.FromArgb(currentColor.ToArgb()),
                    Width = currentWidth,
                    Start = pt,
                    End = pt
                };
            }
            else if (currentTool == "eraser_stroke")
            {
                EraseStrokeNear(pt);
            }
            else if (currentTool == "eraser_area")
            {
                eraseBoxStart = pt;
                activeEraseBox = new RectangleF(pt, new SizeF(0, 0));
            }

            mouseDownPos = new PointF(e.X, e.Y);
            Invalidate();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            PointF pt = ScreenToCanvas(new PointF(e.X, e.Y));

            if (activeStroke != null)
            {
                activeStroke.Points.Add(new StrokePoint { X = pt.X, Y = pt.Y, T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000f });
                Invalidate();
            }
            else if (new[] { "rectangle", "circle", "arrow", "line" }.Contains(currentTool) && shapeStart.HasValue && activeShape != null)
            {
                activeShape.End = pt;
                Invalidate();
            }
            else if (currentTool == "eraser_stroke" && e.Button == MouseButtons.Left)
            {
                EraseStrokeNear(pt);
            }
            else if (currentTool == "eraser_area" && eraseBoxStart.HasValue)
            {
                activeEraseBox = CreateRect(eraseBoxStart.Value, pt);
                Invalidate();
            }
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (activeStroke != null)
            {
                FinalizeStroke(activeStroke);
                activeStroke = null;
            }
            else if (activeShape != null)
            {
                FinalizeShape(activeShape);
                activeShape = null;
                shapeStart = null;
            }
            else if (currentTool == "eraser_area" && activeEraseBox.HasValue)
            {
                EraseArea(activeEraseBox.Value);
                activeEraseBox = null;
                eraseBoxStart = null;
            }

            mouseDownPos = null;
            Invalidate();
        }

        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            float dx = 0, dy = e.Delta > 0 ? -120 : 120;

            if (ModifierKeys.HasFlag(Keys.Control))
            {
                float factor = dy > 0 ? 0.92f : 1.08f;
                ZoomViewport(factor);
            }
            else
            {
                PanViewport(-dx * PAN_SENSITIVITY, -dy * PAN_SENSITIVITY);
            }
        }

        private void FinalizeShape(ShapeItem shape)
        {
            if (shape.Start == shape.End) return;

            if (!pages.ContainsKey(currentPage))
                pages[currentPage] = new Page();

            Page page = pages[currentPage];
            page.Shapes.Add(shape);

            page.PushCommand(new Command(
                undo: () => { if (page.Shapes.Contains(shape)) page.Shapes.Remove(shape); Invalidate(); },
                redo: () => { page.Shapes.Add(shape); Invalidate(); }
            ));

            Invalidate();
        }

        private RectangleF CreateRect(PointF p1, PointF p2)
        {
            return new RectangleF(
                Math.Min(p1.X, p2.X),
                Math.Min(p1.Y, p2.Y),
                Math.Abs(p1.X - p2.X),
                Math.Abs(p1.Y - p2.Y)
            );
        }

        // ======================================================================
        // PAINT EVENT
        // ======================================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // Fill background
            g.Clear(BackColor);

            float s = FitScale();
            PointF off = OriginOffset();
            RectangleF canvasScreenRect = new RectangleF(off.X, off.Y, CANVAS_W * s, CANVAS_H * s);

            // Draw canvas background
            using (var brush = new SolidBrush(bgColor))
                g.FillRectangle(brush, canvasScreenRect);

            if (gridEnabled)
                DrawGrid(g, canvasScreenRect, s);

            // Transform for canvas coordinates
            g.TranslateTransform(off.X, off.Y);
            g.ScaleTransform(s, s);

            // Get current page
            if (!pages.TryGetValue(currentPage, out Page page))
                page = new Page();

            // Draw images
            foreach (var img in page.Images)
            {
                g.DrawImage(img.Pixmap, img.Rect);
            }

            // Draw shapes
            foreach (var shape in page.Shapes)
                DrawShape(g, shape);
            if (activeShape != null)
                DrawShape(g, activeShape);

            // Draw strokes
            foreach (var stroke in page.Strokes)
                if (!stroke.IsErased)
                    DrawStroke(g, stroke);
            foreach (var stroke in networkStrokes.Values)
                DrawStroke(g, stroke);
            if (activeStroke != null)
                DrawStroke(g, activeStroke);

            // Draw viewport rect
            DrawViewportRect(g);

            // Draw erase box
            if (activeEraseBox.HasValue)
                DrawEraseBox(g, activeEraseBox.Value);

            // Draw eraser radius
            if (currentTool == "eraser_stroke")
            {
                PointF cursorPos = PointToClient(Cursor.Position);
                PointF canvasPos = ScreenToCanvas(cursorPos);
                DrawEraserRadius(g, canvasPos);
            }

            g.ResetTransform();
        }

        private void DrawGrid(Graphics g, RectangleF rect, float s)
        {
            float step = 120f * s;
            if (step <= 0) return;

            bool light = bgColor.GetBrightness() > 0.5f;
            using (Pen pen = new Pen(light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255)))
            {
                pen.Width = 1f;
                float x = rect.X;
                while (x < rect.Right)
                {
                    g.DrawLine(pen, x, rect.Top, x, rect.Bottom);
                    x += step;
                }
                float y = rect.Top;
                while (y < rect.Bottom)
                {
                    g.DrawLine(pen, rect.Left, y, rect.Right, y);
                    y += step;
                }
            }
        }

        private void DrawShape(Graphics g, ShapeItem shape)
        {
            using (Pen pen = new Pen(shape.Color, shape.Width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch (shape.ShapeType)
                {
                    case "rectangle":
                        RectangleF rect = CreateRect(shape.Start, shape.End);
                        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                        break;
                    case "circle":
                        float cx = (shape.Start.X + shape.End.X) / 2f;
                        float cy = (shape.Start.Y + shape.End.Y) / 2f;
                        float radius = (float)Math.Sqrt(Math.Pow(shape.End.X - shape.Start.X, 2) + Math.Pow(shape.End.Y - shape.Start.Y, 2)) / 2f;
                        g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
                        break;
                    case "line":
                        g.DrawLine(pen, shape.Start, shape.End);
                        break;
                    case "arrow":
                        DrawArrow(g, pen, shape.Start, shape.End);
                        break;
                }
            }
        }

        private void DrawArrow(Graphics g, Pen pen, PointF start, PointF end)
        {
            g.DrawLine(pen, start, end);

            float angle = (float)Math.Atan2(end.Y - start.Y, end.X - start.X);
            float arrowLength = 20f;
            float arrowAngle = 0.4f;

            PointF p1 = new PointF(
                end.X - arrowLength * (float)Math.Cos(angle - arrowAngle),
                end.Y - arrowLength * (float)Math.Sin(angle - arrowAngle)
            );
            PointF p2 = new PointF(
                end.X - arrowLength * (float)Math.Cos(angle + arrowAngle),
                end.Y - arrowLength * (float)Math.Sin(angle + arrowAngle)
            );

            g.DrawLine(pen, end, p1);
            g.DrawLine(pen, end, p2);
        }

        private void DrawStroke(Graphics g, Stroke stroke)
        {
            if (stroke.Points.Count < 2) return;

            List<PointF> pts = stroke.Points.Select(p => new PointF(p.X, p.Y)).ToList();
            Color color = Color.FromArgb(stroke.Color.ToArgb());

            if (stroke.Tool == "laser")
            {
                int alpha = (int)(255 * stroke.Opacity);
                color = Color.FromArgb(alpha, color);

                using (Pen glowPen = new Pen(Color.FromArgb(alpha / 4, color), stroke.BaseWidth * 3.2f))
                {
                    glowPen.StartCap = LineCap.Round;
                    glowPen.EndCap = LineCap.Round;
                    glowPen.LineJoin = LineJoin.Round;
                    using (GraphicsPath path = BuildSmoothPath(pts, 0.5f))
                        g.DrawPath(glowPen, path);
                }

                using (Pen corePen = new Pen(color, stroke.BaseWidth))
                {
                    corePen.StartCap = LineCap.Round;
                    corePen.EndCap = LineCap.Round;
                    corePen.LineJoin = LineJoin.Round;
                    using (GraphicsPath path = BuildSmoothPath(pts, 0.5f))
                        g.DrawPath(corePen, path);
                }
            }
            else if (stroke.Tool == "paint_brush")
            {
                // DASHED LINE - Square root scaling for natural growth
                float w = stroke.BaseWidth;
                float dashLen = Math.Max(10f, 22f * (float)Math.Sqrt(w));
                float gapLen = Math.Max(5f, 11f * (float)Math.Sqrt(w));

                using (Pen pen = new Pen(color, w))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    pen.DashPattern = new float[] { dashLen, gapLen };
                    pen.DashStyle = DashStyle.Custom;
                    using (GraphicsPath path = BuildSmoothPath(pts, 0.5f))
                        g.DrawPath(pen, path);
                }
            }
            else if (stroke.Tool == "fountain_pen")
            {
                // Fountain pen - varying width
                float baseW = stroke.BaseWidth;
                float minW = baseW * 0.4f;
                float maxW = baseW * 1.5f;
                float nibAngle = 45f * (float)Math.PI / 180f;

                List<float> widths = new();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i < pts.Count - 1)
                    {
                        float angle = (float)Math.Atan2(pts[i + 1].Y - pts[i].Y, pts[i + 1].X - pts[i].X);
                        float w = minW + (maxW - minW) * Math.Abs((float)Math.Cos(angle - nibAngle));
                        float taper = Math.Min(1f, (i + 1) / 8f);
                        w = Math.Max(1f, w * taper);
                        widths.Add(w);
                    }
                    else
                    {
                        widths.Add(widths.Count > 0 ? widths[widths.Count - 1] : baseW);
                    }
                }

                // Smooth widths
                List<float> smoothedWidths = new();
                for (int i = 0; i < widths.Count; i++)
                {
                    int start = Math.Max(0, i - 3);
                    int end = Math.Min(widths.Count, i + 4);
                    float avg = widths.Skip(start).Take(end - start).Average();
                    smoothedWidths.Add(avg);
                }

                // Draw with varying width
                using (Pen pen = new Pen(color, smoothedWidths[0]))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;

                    if (pts.Count < 3)
                    {
                        if (pts.Count >= 2)
                        {
                            pen.Width = smoothedWidths[0];
                            g.DrawLine(pen, pts[0], pts[pts.Count - 1]);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < pts.Count - 1; i++)
                        {
                            pen.Width = smoothedWidths[i];
                            g.DrawLine(pen, pts[i], pts[i + 1]);
                        }
                    }
                }
            }
            else
            {
                // Standard pen
                using (Pen pen = new Pen(color, stroke.BaseWidth))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    using (GraphicsPath path = BuildSmoothPath(pts, 0.5f))
                        g.DrawPath(pen, path);
                }
            }
        }

        private GraphicsPath BuildSmoothPath(List<PointF> pts, float smoothing)
        {
            GraphicsPath path = new GraphicsPath();
            if (pts.Count == 0) return path;

            path.AddLine(pts[0], pts[0]);

            if (pts.Count < 3)
            {
                path.AddLine(pts[0], pts[pts.Count - 1]);
                return path;
            }

            if (smoothing > 0.3f)
            {
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    PointF p0 = pts[i];
                    PointF p1 = pts[i + 1];
                    PointF mid = new PointF((p0.X + p1.X) / 2f, (p0.Y + p1.Y) / 2f);
                    path.AddCurve(new PointF[] { p0, mid });
                }
                path.AddLine(pts[pts.Count - 1], pts[pts.Count - 1]);
            }
            else
            {
                for (int i = 1; i < pts.Count; i++)
                    path.AddLine(pts[i - 1], pts[i]);
            }

            return path;
        }

        private void DrawViewportRect(Graphics g)
        {
            using (Pen pen = new Pen(Color.FromArgb(0x58, 0x65, 0xF2)))
            {
                pen.Width = 3f;
                pen.DashStyle = DashStyle.Dash;
                pen.DashPattern = new float[] { 10, 7 };
                pen.DashOffset = dashPhase;

                g.DrawRectangle(pen, viewportRect.X, viewportRect.Y, viewportRect.Width, viewportRect.Height);
            }
        }

        private void DrawEraseBox(Graphics g, RectangleF rect)
        {
            using (Pen pen = new Pen(Color.FromArgb(0xFF, 0x55, 0x55)))
            {
                pen.Width = 2f;
                pen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }

            using (Brush brush = new SolidBrush(Color.FromArgb(40, 255, 85, 85)))
            {
                g.FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }

        private void DrawEraserRadius(Graphics g, PointF center)
        {
            using (Pen pen = new Pen(Color.FromArgb(150, 255, 100, 100)))
            {
                pen.Width = 1.5f;
                pen.DashStyle = DashStyle.Dash;
                g.DrawEllipse(pen, center.X - eraserRadius, center.Y - eraserRadius, eraserRadius * 2, eraserRadius * 2);
            }

            using (Brush brush = new SolidBrush(Color.FromArgb(30, 255, 100, 100)))
            {
                g.FillEllipse(brush, center.X - eraserRadius, center.Y - eraserRadius, eraserRadius * 2, eraserRadius * 2);
            }
        }
    }

    // ==========================================================================
    // FLOATING TOOLBAR
    // ==========================================================================
    public class FloatingToolbar : UserControl
    {
        private SmartboardCanvas canvas;
        private FlowLayoutPanel panel;
        private Dictionary<string, ToolButton> toolButtons = new();
        private ToolButton penButton;
        private ToolButton paintBrushButton;
        private ToolButton fountainPenButton;
        private ToolButton laserButton;
        private ToolButton eraserButton;
        private ToolButton selectButton;
        private ToolButton shapesButton;
        private ToolButton undoButton;
        private ToolButton redoButton;
        private ToolButton clearButton;
        private ToolButton noteButton;
        private ToolButton backgroundButton;
        private ToolButton exportButton;
        private Label pageLabel;
        private ToolButton prevPageButton;
        private ToolButton nextPageButton;
        private Label statusDot;

        public FloatingToolbar(SmartboardCanvas canvas)
        {
            this.canvas = canvas;
            BackColor = Color.FromArgb(219, 28, 30, 34);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            panel = new FlowLayoutPanel();
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.FlowDirection = FlowDirection.LeftToRight;
            panel.Padding = new Padding(10, 6, 10, 6);
            panel.BackColor = Color.Transparent;

            Controls.Add(panel);

            // Create buttons
            CreateButtons();

            // Position
            UpdatePosition();

            // Drop shadow effect
            // In WinForms we can use a simple border
            Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(20, 255, 255, 255), 1))
                {
                    e.Graphics.DrawRoundedRectangle(pen, 0, 0, Width - 1, Height - 1, 20);
                }
            };
        }

        private void CreateButtons()
        {
            // Page controls
            prevPageButton = CreateButton("\u25C0", "Previous Page", () => { /* Implement page navigation */ });
            panel.Controls.Add(prevPageButton);

            pageLabel = new Label();
            pageLabel.Text = "Page 1";
            pageLabel.ForeColor = Color.FromArgb(0xE4, 0xE6, 0xEB);
            pageLabel.BackColor = Color.Transparent;
            pageLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            pageLabel.AutoSize = true;
            pageLabel.Padding = new Padding(8, 4, 8, 4);
            pageLabel.MouseClick += (s, e) => { /* Show page selector */ };
            panel.Controls.Add(pageLabel);

            nextPageButton = CreateButton("\u25B6", "Next Page", () => { /* Implement page navigation */ });
            panel.Controls.Add(nextPageButton);

            panel.Controls.Add(CreateSeparator());

            // Undo/Redo
            undoButton = CreateButton("\u21A9", "Undo (Ctrl+Z)", () => canvas.Undo());
            panel.Controls.Add(undoButton);
            redoButton = CreateButton("\u21AA", "Redo (Ctrl+Y)", () => canvas.Redo());
            panel.Controls.Add(redoButton);

            panel.Controls.Add(CreateSeparator());

            // Tools
            penButton = CreateButton("\u270E", "Pen", () => canvas.SetTool("pen"), true);
            panel.Controls.Add(penButton);
            paintBrushButton = CreateButton("\uD83E\uDD8C", "Paint Brush", () => canvas.SetTool("paint_brush"));
            panel.Controls.Add(paintBrushButton);
            fountainPenButton = CreateButton("\uD83D\uDD8B", "Fountain Pen", () => canvas.SetTool("fountain_pen"));
            panel.Controls.Add(fountainPenButton);
            laserButton = CreateButton("\u26A1", "Laser (Fades)", () => canvas.SetTool("laser"));
            panel.Controls.Add(laserButton);

            panel.Controls.Add(CreateSeparator());

            // Shapes
            shapesButton = CreateButton("\u25AD", "Shapes", () => canvas.SetTool("rectangle"));
            panel.Controls.Add(shapesButton);

            panel.Controls.Add(CreateSeparator());

            // Eraser
            eraserButton = CreateButton("\u2422", "Eraser", () => canvas.SetTool("eraser_stroke"));
            panel.Controls.Add(eraserButton);

            panel.Controls.Add(CreateSeparator());

            // Clear / Note
            clearButton = CreateButton("\uD83D\uDDD1", "Clear Page", () => canvas.ClearCurrentPage());
            panel.Controls.Add(clearButton);
            noteButton = CreateButton("\uD83D\uDCDD", "Add Sticky Note", () => { /* Add note */ });
            panel.Controls.Add(noteButton);

            panel.Controls.Add(CreateSeparator());

            // Background
            backgroundButton = CreateButton("\u25A3", "Board Background", () => { /* Show background picker */ });
            panel.Controls.Add(backgroundButton);

            panel.Controls.Add(CreateSeparator());

            // Export
            exportButton = CreateButton("\uD83D\uDDBC", "Export", () => { /* Export dialog */ });
            panel.Controls.Add(exportButton);

            // Status dot
            statusDot = new Label();
            statusDot.Text = "\u25CF";
            statusDot.ForeColor = Color.FromArgb(0x72, 0x76, 0x7D);
            statusDot.BackColor = Color.Transparent;
            statusDot.AutoSize = true;
            statusDot.Font = new Font("Segoe UI", 14f);
            statusDot.Padding = new Padding(4, 0, 4, 0);
            panel.Controls.Add(statusDot);
        }

        private ToolButton CreateButton(string text, string tooltip, Action onClick, bool checkedState = false)
        {
            var btn = new ToolButton();
            btn.Text = text;
            btn.ToolTipText = tooltip;
            btn.CheckOnClick = true;
            btn.Checked = checkedState;
            btn.Click += (s, e) => onClick?.Invoke();
            btn.BackColor = Color.Transparent;
            btn.ForeColor = Color.FromArgb(0xE4, 0xE6, 0xEB);
            btn.Font = new Font("Segoe UI", 12f);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.CheckedBackColor = Color.FromArgb(0x58, 0x65, 0xF2);
            btn.Size = new Size(36, 36);
            btn.Padding = new Padding(0);
            btn.Margin = new Padding(2);
            btn.Cursor = Cursors.Hand;

            btn.MouseEnter += (s, e) => { btn.BackColor = Color.FromArgb(30, 255, 255, 255); };
            btn.MouseLeave += (s, e) => { btn.BackColor = Color.Transparent; };

            return btn;
        }

        private Panel CreateSeparator()
        {
            return new Panel
            {
                Size = new Size(1, 28),
                BackColor = Color.FromArgb(38, 255, 255, 255),
                Margin = new Padding(4, 4, 4, 4)
            };
        }

        public void UpdatePosition()
        {
            if (Parent == null) return;

            int x = (Parent.Width - Width) / 2;
            int y = Parent.Height - Height - 24;
            Location = new Point(Math.Max(0, x), Math.Max(0, y));
        }

        public void SetPageLabel(int pageNum)
        {
            pageLabel.Text = $"Page {pageNum}";
        }

        public void SetConnectionStatus(bool connected, string ip = "")
        {
            statusDot.ForeColor = connected ? Color.FromArgb(0x57, 0xF2, 0x87) : Color.FromArgb(0x72, 0x76, 0x7D);
            statusDot.ToolTipText = connected ? $"Phone connected: {ip}" : "No phone connected";
        }
    }

    // ==========================================================================
    // TOOL BUTTON
    // ==========================================================================
    public class ToolButton : Button
    {
        private string tooltipText = "";

        public string ToolTipText
        {
            get => tooltipText;
            set { tooltipText = value; }
        }

        protected override void OnMouseHover(EventArgs e)
        {
            base.OnMouseHover(e);
            if (!string.IsNullOrEmpty(tooltipText))
            {
                ToolTip tooltip = new ToolTip();
                tooltip.SetToolTip(this, tooltipText);
            }
        }
    }

    // ==========================================================================
    // DATA MODELS
    // ==========================================================================
    public class Stroke
    {
        public string Id { get; set; }
        public string Tool { get; set; }
        public Color Color { get; set; }
        public float BaseWidth { get; set; }
        public List<StrokePoint> Points { get; set; } = new();
        public float Created { get; set; } = (float)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        public float? FadeStart { get; set; }
        public float Opacity { get; set; } = 1.0f;
        public int Layer { get; set; } = 0;
        public bool IsErased { get; set; } = false;
    }

    public class StrokePoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float T { get; set; }
    }

    public class ShapeItem
    {
        public string Id { get; set; }
        public string ShapeType { get; set; }
        public Color Color { get; set; }
        public float Width { get; set; }
        public PointF Start { get; set; }
        public PointF End { get; set; }
        public int Layer { get; set; } = 0;
    }

    public class ImageItem
    {
        public string Id { get; set; }
        public Image Pixmap { get; set; }
        public RectangleF Rect { get; set; }
        public int Layer { get; set; } = 0;
    }

    public class StickyNoteData
    {
        public string Id { get; set; }
        public RectangleF Rect { get; set; }
        public string Text { get; set; } = "";
        public string Color { get; set; } = "#FFF7B2";
        public int FontSize { get; set; } = 14;
        public int Layer { get; set; } = 0;
        public bool IsDeleted { get; set; } = false;
    }

    public class Page
    {
        public List<Stroke> Strokes { get; set; } = new();
        public List<ShapeItem> Shapes { get; set; } = new();
        public List<ImageItem> Images { get; set; } = new();
        public List<StickyNoteData> Notes { get; set; } = new();
        public Stack<Command> UndoStack { get; set; } = new();
        public Stack<Command> RedoStack { get; set; } = new();

        public void PushCommand(Command cmd)
        {
            UndoStack.Push(cmd);
            RedoStack.Clear();
        }

        public void Undo()
        {
            if (UndoStack.Count == 0) return;
            var cmd = UndoStack.Pop();
            cmd.Undo();
            RedoStack.Push(cmd);
        }

        public void Redo()
        {
            if (RedoStack.Count == 0) return;
            var cmd = RedoStack.Pop();
            cmd.Redo();
            UndoStack.Push(cmd);
        }
    }

    public class Command
    {
        private Action undoAction;
        private Action redoAction;

        public Command(Action undo, Action redo)
        {
            undoAction = undo;
            redoAction = redo;
        }

        public void Undo() => undoAction?.Invoke();
        public void Redo() => redoAction?.Invoke();
    }

    // ==========================================================================
    // EXTENSION METHODS
    // ==========================================================================
    public static class GraphicsExtensions
    {
        public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float width, float height, float radius)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
                path.AddArc(x + width - radius * 2, y, radius * 2, radius * 2, 270, 90);
                path.AddArc(x + width - radius * 2, y + height - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(x, y + height - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                g.DrawPath(pen, path);
            }
        }

        public static void FillRoundedRectangle(this Graphics g, Brush brush, float x, float y, float width, float height, float radius)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
                path.AddArc(x + width - radius * 2, y, radius * 2, radius * 2, 270, 90);
                path.AddArc(x + width - radius * 2, y + height - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(x, y + height - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }
}