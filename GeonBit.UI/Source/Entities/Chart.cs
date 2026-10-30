#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using GeonBit.UI.DataTypes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GeonBit.UI.Entities
{
    public enum ChartKind
    {
        Bars,
        Meter,

        // Every series shares one band, drawn in list order, so a shorter series leaves the one
        // behind it visible past its end.
        Overlay,

        // The kinds below carry one row of samples per series and draw their own axes.
        Line,
        StackedArea,
        Column,
        StackedColumn,

        // One value per series, drawn as wedges of a circle with the share beside each label.
        Pie,
    }

    public sealed class ChartSeries
    {
        public string Label = "";
        public Color Color = Color.White;

        // True when a colour was declared for this series, which a meter keeps instead of
        // taking the shared colour scale.
        public bool ColorSet;

        // The value the chart is heading for, and the value it draws right now: every change
        // tweens the drawn value from where it is to the target, so a chart never jumps.
        public float Value;
        public float Shown;

        // One sample per axis slot, the samples drawn at this moment, and the samples the
        // running tween started from.
        public float[] Samples = Array.Empty<float>();
        public float[] ShownSamples = Array.Empty<float>();
        internal float[] FromSamples = Array.Empty<float>();
    }

    /// <summary>
    /// A chart on a shared scale. Each series is one row, filled from the left, and every
    /// filled length is drawn from Shown against ShownMax, both of which ease towards their
    /// targets over AnimateMillis. The axis kinds instead take one row of samples per series and
    /// draw their own value axis, so their ShownMax and ShownMin are the range the axis holds.
    /// </summary>
    public class Chart : Entity
    {
        public ChartKind Kind = ChartKind.Bars;
        public readonly List<ChartSeries> Series = new List<ChartSeries>();

        /// <summary>The palette a meter's fill takes from its fraction, unless the chart sets its own.</summary>
        public static Func<float, Color>? DefaultColorScale;

        /// <summary>The colours an axis series takes when it declared none, in list order.</summary>
        public static readonly Color[] Palette =
        {
            new Color(112, 168, 236),
            new Color(236, 172, 92),
            new Color(112, 200, 178),
            new Color(190, 148, 236),
            new Color(232, 128, 152),
            new Color(168, 196, 108),
            new Color(120, 198, 226),
            new Color(226, 206, 120),
        };

        private float _min;

        public int BarHeight = 13;
        public int BarGap = 2;
        public int AnimateMillis = 400;

        // The unfilled length of each row, so a short bar still reads as a share of the ceiling.
        public bool ShowTrack;
        public Color TrackColor = Color.White;
        public float TrackOpacity = 0.08f;

        /// <summary>Colours a fill from its fraction of the scale; null uses DefaultColorScale.</summary>
        public Func<float, Color>? ColorScale;

        // The axis kinds. ShowAxis draws the labels, the axis lines and the minor ticks; with it
        // off the chart is a bare plot, which is what a card wants.
        public bool ShowAxis = true;
        public bool ShowLegend = true;
        public bool ShowGrid = true;

        /// <summary>Fixed axis ends; NaN takes the range from the samples.</summary>
        public float AxisMin = float.NaN;
        public float AxisMax = float.NaN;

        /// <summary>One label per sample, thinned to as many as the plot can hold.</summary>
        public string[] XLabels = Array.Empty<string>();

        /// <summary>Formats a value-axis tick; null uses the step's own decimals.</summary>
        public Func<float, string>? AxisLabel;

        public Color TextColor = new Color(222, 226, 233);
        public Color GridColor = Color.White;
        public Color AxisColor = Color.White;
        public float GridOpacity = 0.10f;
        public float AxisOpacity = 0.32f;
        public int LabelSize = 12;
        public int LineWidth = 1;

        private float _targetMin;
        private float _shownMin;

        private float _targetMax = 1f;
        private float _shownMax = 1f;

        private float[] _from = Array.Empty<float>();
        private bool _valuesTween;
        private double _valuesStart;
        private double _valuesEnd;

        private float _scaleFrom = 1f;
        private float _minFrom;
        private bool _scaleTween;
        private double _scaleStart;
        private double _scaleEnd;

        private static readonly float[] Mantissas = { 1f, 2f, 2.5f, 5f, 10f };

        public Chart(Vector2? size = null, Anchor anchor = Anchor.Auto, Vector2? offset = null)
            : base(size ?? new Vector2(160f, 13f), anchor, offset)
        {
            Padding = Vector2.Zero;
        }

        public float Min
        {
            get => _min;
            set
            {
                _min = value;
                if (_scaleTween)
                    return;
                _shownMin = value;
                _targetMin = value;
            }
        }

        public float Max
        {
            get => _targetMax;
            set
            {
                if (value == _targetMax)
                    return;
                _targetMax = value;
                if (_scaleTween)
                    StartScaleTween();
                else
                    _shownMax = value;
            }
        }

        /// <summary>The scale the bars are currently drawn against; for an axis chart, its top.</summary>
        public float ShownMax => _shownMax;

        /// <summary>The bottom of the drawn axis; a bar chart's Min.</summary>
        public float ShownMin => _shownMin;

        public bool HasAxes => Kind == ChartKind.Line || Kind == ChartKind.StackedArea
                               || Kind == ChartKind.Column || Kind == ChartKind.StackedColumn;

        public ChartSeries AddSeries(string label, Color color)
        {
            var series = new ChartSeries { Label = label, Color = color, ColorSet = true };
            Series.Add(series);
            _from = new float[Series.Count];
            return series;
        }

        /// <summary>Adds a series that takes its colour from Palette by its position.</summary>
        public ChartSeries AddSeries(string label)
        {
            var series = new ChartSeries { Label = label };
            Series.Add(series);
            _from = new float[Series.Count];
            return series;
        }

        /// <summary>Drops every series, for data whose shape does not match the last page.</summary>
        public void ClearSeries()
        {
            Series.Clear();
            _from = Array.Empty<float>();
            _valuesTween = false;
            _scaleTween = false;
            XLabels = Array.Empty<string>();
        }

        /// <summary>Assign targets and ease the drawn values towards them.</summary>
        public void SetTargets(params float[] values) => Assign(values, animate: true);

        /// <summary>Assign values that are already true, with no animation.</summary>
        public void SetValues(params float[] values) => Assign(values, animate: false);

        /// <summary>Assign one row of samples per series, easing towards them.</summary>
        public void SetSamples(params float[][] rows) => AssignSamples(rows, animate: true);

        /// <summary>Assign samples that are already true, with no animation.</summary>
        public void SetSamplesNow(params float[][] rows) => AssignSamples(rows, animate: false);

        /// <summary>Drop every bar to the floor and grow back to the current targets.</summary>
        public void Restart()
        {
            foreach (var series in Series)
                series.Shown = Min;

            if (HasAxes)
            {
                Range(out float low, out float high);
                foreach (var series in Series)
                {
                    if (series.ShownSamples.Length == series.Samples.Length)
                        Array.Clear(series.ShownSamples, 0, series.ShownSamples.Length);
                    else
                        series.ShownSamples = new float[series.Samples.Length];
                }

                _targetMin = low;
                _targetMax = high;
                _shownMin = low;
                _shownMax = low + Math.Max(1f, (high - low) * 0.02f);
                StartScaleTween();
            }

            StartValuesTween();
        }

        public override void Update(ref Entity targetEntity, ref Entity dragTargetEntity,
                                    ref bool wasEventHandled, Point scrollVal)
        {
            if (Visible)
                Advance();
            base.Update(ref targetEntity, ref dragTargetEntity, ref wasEventHandled, scrollVal);
        }

        protected override void DrawEntity(SpriteBatch spriteBatch, DrawPhase phase)
        {
            if (phase != DrawPhase.Base || Series.Count == 0)
                return;

            var rect = _destRectInternal;
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            if (HasAxes)
            {
                DrawAxes(spriteBatch, rect);
                return;
            }

            if (Kind == ChartKind.Pie)
            {
                DrawPie(spriteBatch, rect);
                return;
            }

            if (_shownMax <= _shownMin)
                return;

            if (Kind == ChartKind.Meter)
                DrawMeter(spriteBatch, rect);
            else if (Kind == ChartKind.Overlay)
                DrawOverlay(spriteBatch, rect);
            else
                DrawBars(spriteBatch, rect);
        }

        private void DrawOverlay(SpriteBatch spriteBatch, Rectangle rect)
        {
            int height = Math.Min(BarHeight, rect.Height);
            foreach (var series in Series)
            {
                int width = (int)Math.Round(rect.Width * Fraction(series.Shown));
                if (width <= 0)
                    continue;
                var band = new Rectangle(rect.X, rect.Y, width, height);
                spriteBatch.Draw(Resources.Instance.WhiteTexture, band, ColorOf(series));
            }
        }

        private void DrawMeter(SpriteBatch spriteBatch, Rectangle rect)
        {
            if (ShowTrack)
                spriteBatch.Draw(Resources.Instance.WhiteTexture, rect, TrackColor * TrackOpacity);

            int width = (int)Math.Round(rect.Width * Fraction(Series[0].Shown));
            if (width > 0)
            {
                var fill = new Rectangle(rect.X, rect.Y, width, rect.Height);
                spriteBatch.Draw(Resources.Instance.WhiteTexture, fill, ColorOf(Series[0]));
            }
        }

        private void DrawBars(SpriteBatch spriteBatch, Rectangle rect)
        {
            int stride = BarHeight + BarGap;
            for (int i = 0; i < Series.Count; i++)
            {
                int y = rect.Y + (i * stride);
                if (y + BarHeight > rect.Bottom)
                    break;

                if (ShowTrack)
                {
                    var track = new Rectangle(rect.X, y, rect.Width, BarHeight);
                    spriteBatch.Draw(Resources.Instance.WhiteTexture, track, TrackColor * TrackOpacity);
                }

                int width = (int)Math.Round(rect.Width * Fraction(Series[i].Shown));
                if (width > 0)
                {
                    var bar = new Rectangle(rect.X, y, width, BarHeight);
                    spriteBatch.Draw(Resources.Instance.WhiteTexture, bar, ColorOf(Series[i]));
                }
            }
        }

        private void DrawPie(SpriteBatch spriteBatch, Rectangle rect)
        {
            float total = 0f;
            for (int i = 0; i < Series.Count; i++)
            {
                float value = Series[i].Shown;
                if (value > 0f && !float.IsNaN(value))
                    total += value;
            }
            if (total <= 0f)
                return;

            var face = Resources.Instance.CaptionLadder.Pick(LabelSize * GlobalScale);
            if (face == null)
                return;
            float textScale = LabelSize * GlobalScale / face.PixelSize;

            int legendWidth = ShowLegend ? PieLegendWidth(face, textScale, rect.Width) : 0;
            int diameter = Math.Min(rect.Height, rect.Width - legendWidth) - 2;
            if (diameter < 8)
                return;
            int radius = diameter / 2;
            int centreX = rect.X + radius + 1;
            int centreY = rect.Y + (rect.Height / 2);

            var edges = new float[Series.Count];
            float running = 0f;
            for (int i = 0; i < Series.Count; i++)
            {
                float value = Series[i].Shown;
                if (value > 0f && !float.IsNaN(value))
                    running += value;
                edges[i] = running / total;
            }

            var cuts = new List<int>();
            for (int y = centreY - radius; y <= centreY + radius; y++)
            {
                int offset = y - centreY;
                if (offset == 0)
                    offset = -1;
                float spanSquared = ((float)radius * radius) - ((float)offset * offset);
                if (spanSquared <= 0f)
                    continue;
                int span = (int)Math.Sqrt(spanSquared);
                int left = centreX - span;
                int right = centreX + span;

                cuts.Clear();
                for (int i = 0; i < edges.Length - 1; i++)
                {
                    float boundary = edges[i] * 360f;
                    if (boundary <= 0f || boundary >= 360f)
                        continue;
                    float x = centreX + ((-offset) * MathF.Tan(boundary * MathF.PI / 180f));
                    if (!(x > left && x < right))
                        continue;
                    cuts.Add((int)Math.Round(x));
                }
                cuts.Sort();

                int startX = left;
                foreach (var cut in cuts)
                {
                    if (cut <= startX || cut > right)
                        continue;
                    int slice = SliceAt(AngleOf(((startX + cut) / 2) - centreX, offset), edges);
                    spriteBatch.Draw(Resources.Instance.WhiteTexture,
                        new Rectangle(startX, y, cut - startX, 1), AxisColorOf(slice));
                    startX = cut;
                }

                if (startX <= right)
                {
                    int slice = SliceAt(AngleOf(((startX + right) / 2) - centreX, offset), edges);
                    spriteBatch.Draw(Resources.Instance.WhiteTexture,
                        new Rectangle(startX, y, right - startX + 1, 1), AxisColorOf(slice));
                }
            }

            if (legendWidth > 0)
                DrawPieLegend(spriteBatch, rect, face, textScale, legendWidth, total);
        }

        private int PieLegendWidth(FontFace face, float textScale, int width)
        {
            int widest = 0;
            foreach (var series in Series)
            {
                if (string.IsNullOrEmpty(series.Label))
                    continue;
                var text = $"{series.Label}  100%";
                widest = Math.Max(widest, (int)Math.Ceiling(face.Font.MeasureString(text).X * textScale));
            }
            if (widest == 0)
                return 0;
            return Math.Min(widest + 16, width / 2);
        }

        private void DrawPieLegend(SpriteBatch spriteBatch, Rectangle rect, FontFace face, float textScale,
                                   int legendWidth, float total)
        {
            int swatch = Math.Max(3, (int)Math.Round(6 * GlobalScale));
            float lineHeight = (face.PixelSize * textScale) + 4;
            float left = rect.Right - legendWidth + 10;
            float y = rect.Y + 2;

            for (int i = 0; i < Series.Count; i++)
            {
                var series = Series[i];
                if (string.IsNullOrEmpty(series.Label))
                    continue;
                if (y + lineHeight > rect.Bottom)
                    break;

                spriteBatch.Draw(Resources.Instance.WhiteTexture,
                    new Rectangle((int)left, (int)y + 2, swatch, swatch), AxisColorOf(i));

                float value = series.Shown > 0f && !float.IsNaN(series.Shown) ? series.Shown : 0f;
                int percent = (int)Math.Round((value / total) * 100f);
                spriteBatch.DrawString(face.Font, $"{series.Label}  {percent}%",
                    new Vector2(left + swatch + 4, y), TextColor,
                    0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
                y += lineHeight;
            }
        }

        private static float AngleOf(int dx, int dy)
        {
            double degrees = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
            if (degrees < 0)
                degrees += 360.0;
            return (float)degrees;
        }

        private static int SliceAt(float angle, float[] edges)
        {
            float fraction = angle / 360f;
            for (int i = 0; i < edges.Length; i++)
                if (fraction < edges[i])
                    return i;
            return edges.Length - 1;
        }

        private void DrawAxes(SpriteBatch spriteBatch, Rectangle rect)
        {
            float low = _shownMin;
            float high = _shownMax;
            if (!(high > low))
                return;

            var face = Resources.Instance.CaptionLadder.Pick(LabelSize * GlobalScale);
            if (face == null)
                return;
            float textScale = LabelSize * GlobalScale / face.PixelSize;
            float textHeight = face.PixelSize * textScale;

            int legendHeight = ShowLegend ? LegendHeight(face, textScale) : 0;
            int bottomBand = ShowAxis ? (int)Math.Ceiling(textHeight) + 3 : 0;
            int plotTop = rect.Y + legendHeight + 1;
            int plotHeight = rect.Bottom - bottomBand - plotTop;
            if (plotHeight < 6)
                return;

            float step = ChooseStep(high - low, Math.Max(2, plotHeight / 26));
            var ticks = TickValues(low, high, step);

            int labelWidth = 0;
            if (ShowAxis)
                foreach (var tick in ticks)
                    labelWidth = Math.Max(labelWidth,
                        (int)Math.Ceiling(face.Font.MeasureString(FormatTick(tick, step)).X * textScale));

            int plotLeft = rect.X + (ShowAxis ? Math.Min(labelWidth + 6, Math.Max(0, rect.Width / 3)) : 0);
            var plot = new Rectangle(plotLeft, plotTop, rect.Right - 2 - plotLeft, plotHeight);
            if (plot.Width < 6)
                return;

            DrawLegend(spriteBatch, rect, face, textScale, legendHeight);

            int zero = Math.Clamp(YOf(0f, plot), plot.Top, plot.Bottom - 1);

            if (ShowAxis)
                DrawXLabels(spriteBatch, plot, rect, face, textScale, textHeight);

            if (ShowGrid)
            {
                foreach (var tick in ticks)
                {
                    int y = YOf(tick, plot);
                    if (y < plot.Top || y > plot.Bottom - 1)
                        continue;
                    spriteBatch.Draw(Resources.Instance.WhiteTexture,
                                     new Rectangle(plot.X, y, plot.Width, 1),
                                     GridColor * GridOpacity);
                }
                if (low < 0f && high > 0f)
                    spriteBatch.Draw(Resources.Instance.WhiteTexture,
                                     new Rectangle(plot.X, zero, plot.Width, 1),
                                     GridColor * (GridOpacity * 1.8f));
            }

            if (Kind == ChartKind.Line)
                DrawLineSeries(spriteBatch, plot);
            else if (Kind == ChartKind.StackedArea)
                DrawStackedArea(spriteBatch, plot);
            else
                DrawColumns(spriteBatch, plot, zero);

            if (ShowAxis)
                DrawAxisMarks(spriteBatch, plot, face, textScale, textHeight, ticks, step, rect);
        }

        private void DrawXLabels(SpriteBatch spriteBatch, Rectangle plot, Rectangle rect, FontFace face,
                                 float textScale, float textHeight)
        {
            int count = SampleCount();
            if (count == 0 || XLabels.Length == 0)
                return;

            int widest = 0;
            foreach (var label in XLabels)
            {
                if (string.IsNullOrEmpty(label))
                    continue;
                widest = Math.Max(widest, (int)Math.Ceiling(face.Font.MeasureString(label).X * textScale));
            }
            if (widest == 0)
                return;

            int slots = Math.Max(1, plot.Width / (widest + 14));
            int stride = Math.Max(1, (int)Math.Ceiling((double)count / slots));
            bool centred = Kind == ChartKind.Column || Kind == ChartKind.StackedColumn;

            for (int index = 0; index < count && index < XLabels.Length; index += stride)
            {
                var label = XLabels[index];
                if (string.IsNullOrEmpty(label))
                    continue;

                float width = face.Font.MeasureString(label).X * textScale;
                int x = LabelX(index, count, plot, centred) - (int)Math.Round(width * 0.5f);
                if (x < rect.X || x + width > rect.Right - 1)
                    continue;

                if (ShowGrid)
                    spriteBatch.Draw(Resources.Instance.WhiteTexture,
                                     new Rectangle(x + (int)Math.Round(width * 0.5f), plot.Top, 1, plot.Height),
                                     GridColor * (GridOpacity * 0.6f));

                spriteBatch.DrawString(face.Font, label, new Vector2(x, plot.Bottom + 2), TextColor,
                    0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            }
        }

        private void DrawAxisMarks(SpriteBatch spriteBatch, Rectangle plot, FontFace face, float textScale,
                                   float textHeight, List<float> ticks, float step, Rectangle rect)
        {
            var color = AxisColor * AxisOpacity;
            spriteBatch.Draw(Resources.Instance.WhiteTexture,
                             new Rectangle(plot.X - 1, plot.Top, 1, plot.Height + 1), color);
            spriteBatch.Draw(Resources.Instance.WhiteTexture,
                             new Rectangle(plot.X - 1, plot.Bottom, plot.Width + 1, 1), color);

            foreach (var tick in ticks)
            {
                int y = YOf(tick, plot);
                if (y < plot.Top || y > plot.Bottom - 1)
                    continue;
                spriteBatch.Draw(Resources.Instance.WhiteTexture,
                                 new Rectangle(plot.X - 4, y, 3, 1), color);

                var text = FormatTick(tick, step);
                float width = face.Font.MeasureString(text).X * textScale;
                float x = plot.X - 6 - width;
                if (x < rect.X)
                    continue;
                spriteBatch.DrawString(face.Font, text, new Vector2(x, y - (textHeight * 0.5f)), TextColor,
                    0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            }

            float minor = MinorStep(step);
            float first = (float)Math.Ceiling(_shownMin / minor - 1e-3f) * minor;
            for (float value = first; value <= _shownMax; value += minor)
            {
                float major = (float)Math.Round(value / step) * step;
                if (Math.Abs(value - major) < minor * 0.01f)
                    continue;
                int y = YOf(value, plot);
                if (y < plot.Top || y > plot.Bottom - 1)
                    continue;
                spriteBatch.Draw(Resources.Instance.WhiteTexture,
                                 new Rectangle(plot.X - 3, y, 2, 1), color * 0.6f);
            }
        }

        private void DrawLineSeries(SpriteBatch spriteBatch, Rectangle plot)
        {
            for (int i = 0; i < Series.Count; i++)
            {
                var columns = Resample(Series[i].ShownSamples, plot.Width, sum: false);
                int prior = int.MinValue;
                for (int x = 0; x < columns.Length; x++)
                {
                    if (float.IsNaN(columns[x]))
                    {
                        prior = int.MinValue;
                        continue;
                    }
                    int y = YOf(columns[x], plot);
                    if (prior == int.MinValue)
                        FillColumn(spriteBatch, plot, x, y, y + LineWidth - 1, AxisColorOf(i));
                    else
                        FillColumn(spriteBatch, plot, x, y, prior + LineWidth - 1, AxisColorOf(i));
                    prior = y;
                }
            }
        }

        private void DrawStackedArea(SpriteBatch spriteBatch, Rectangle plot)
        {
            int width = plot.Width;
            var below = new float[width];
            for (int i = 0; i < width; i++)
                below[i] = 0f;

            for (int i = 0; i < Series.Count; i++)
            {
                var columns = Resample(Series[i].ShownSamples, width, sum: false);
                for (int x = 0; x < width; x++)
                {
                    if (float.IsNaN(below[x]) || float.IsNaN(columns[x]))
                    {
                        below[x] = float.NaN;
                        continue;
                    }
                    float top = below[x] + columns[x];
                    FillColumn(spriteBatch, plot, x, YOf(top, plot), YOf(below[x], plot), AxisColorOf(i));
                    below[x] = top;
                }
            }
        }

        private void DrawColumns(SpriteBatch spriteBatch, Rectangle plot, int zero)
        {
            int count = SampleCount();
            if (count == 0)
                return;

            int group = count > plot.Width ? (int)Math.Ceiling((double)count / plot.Width) : 1;
            int slots = (int)Math.Ceiling((double)count / group);
            float slot = (float)plot.Width / slots;
            float groupWidth = Math.Max(1f, slot * 0.72f);

            if (Kind == ChartKind.StackedColumn)
            {
                var bases = new float[slots];
                for (int i = 0; i < Series.Count; i++)
                {
                    var values = Bucket(Series[i].ShownSamples, group, slots);
                    for (int s = 0; s < slots; s++)
                    {
                        float x = plot.X + (s * slot) + ((slot - groupWidth) * 0.5f);
                        if (float.IsNaN(values[s]) || float.IsNaN(bases[s]))
                        {
                            bases[s] = float.NaN;
                            continue;
                        }
                        float top = bases[s] + values[s];
                        FillBar(spriteBatch, plot, x, groupWidth, YOf(top, plot), YOf(bases[s], plot),
                                AxisColorOf(i));
                        bases[s] = top;
                    }
                }
                return;
            }

            float gap = Series.Count > 1 ? 1f : 0f;
            float barWidth = Math.Max(1f, (groupWidth - ((Series.Count - 1) * gap)) / Series.Count);
            for (int i = 0; i < Series.Count; i++)
            {
                var values = Bucket(Series[i].ShownSamples, group, slots);
                for (int s = 0; s < slots; s++)
                {
                    if (float.IsNaN(values[s]))
                        continue;
                    float x = plot.X + (s * slot) + ((slot - groupWidth) * 0.5f) + (i * (barWidth + gap));
                    FillBar(spriteBatch, plot, x, barWidth, YOf(values[s], plot), zero, AxisColorOf(i));
                }
            }
        }

        private int LegendHeight(FontFace face, float textScale)
        {
            foreach (var series in Series)
                if (!string.IsNullOrEmpty(series.Label))
                    return (int)Math.Ceiling(face.PixelSize * textScale) + 4;
            return 0;
        }

        private void DrawLegend(SpriteBatch spriteBatch, Rectangle rect, FontFace face, float textScale,
                                int legendHeight)
        {
            if (legendHeight <= 0)
                return;

            int swatch = Math.Max(3, (int)Math.Round(6 * GlobalScale));
            float right = rect.Right;
            for (int i = Series.Count - 1; i >= 0; i--)
            {
                var series = Series[i];
                if (string.IsNullOrEmpty(series.Label))
                    continue;

                float width = (face.Font.MeasureString(series.Label).X * textScale) + swatch + 3;
                float left = right - width;
                if (left < rect.X)
                    break;

                spriteBatch.Draw(Resources.Instance.WhiteTexture,
                    new Rectangle((int)left, rect.Y + ((legendHeight - swatch) / 2), swatch, swatch),
                    AxisColorOf(i));
                spriteBatch.DrawString(face.Font, series.Label,
                    new Vector2(left + swatch + 3, rect.Y + 2), TextColor,
                    0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
                right = left - 10;
            }
        }

        private static void FillBar(SpriteBatch spriteBatch, Rectangle plot, float left, float width, int a, int b,
                                    Color color)
        {
            int x = (int)Math.Round(left);
            int w = Math.Max(1, (int)Math.Round(width));
            int top = Math.Min(a, b);
            int bottom = Math.Max(a, b);
            top = Math.Max(top, plot.Top);
            bottom = Math.Min(bottom, plot.Bottom - 1);
            if (bottom < top)
                return;
            x = Math.Max(x, plot.X);
            int right = Math.Min(x + w, plot.Right);
            if (right <= x)
                return;
            spriteBatch.Draw(Resources.Instance.WhiteTexture,
                             new Rectangle(x, top, right - x, bottom - top + 1), color);
        }

        private static void FillColumn(SpriteBatch spriteBatch, Rectangle plot, int x, int a, int b, Color color)
        {
            if (x < 0 || x >= plot.Width)
                return;

            int top = Math.Min(a, b);
            int bottom = Math.Max(a, b);
            top = Math.Max(top, plot.Top);
            bottom = Math.Min(bottom, plot.Bottom - 1);
            if (bottom < top)
                return;
            spriteBatch.Draw(Resources.Instance.WhiteTexture,
                             new Rectangle(plot.X + x, top, 1, bottom - top + 1), color);
        }

        private int YOf(float value, Rectangle plot)
        {
            float fraction = (value - _shownMin) / (_shownMax - _shownMin);
            return plot.Bottom - 1 - (int)Math.Round(fraction * (plot.Height - 1));
        }

        private int LabelX(int index, int count, Rectangle plot, bool centred)
        {
            if (count <= 1)
                return plot.X + (plot.Width / 2);
            float fraction = centred ? (index + 0.5f) / count : (float)index / (count - 1);
            return plot.X + (int)Math.Round(fraction * (plot.Width - 1));
        }

        private int SampleCount()
        {
            int count = 0;
            foreach (var series in Series)
                count = Math.Max(count, series.ShownSamples.Length);
            return count;
        }

        private static float[] Resample(float[] samples, int width, bool sum)
        {
            var columns = new float[width];
            int count = samples.Length;
            if (count == 0)
            {
                for (int i = 0; i < width; i++)
                    columns[i] = float.NaN;
                return columns;
            }
            if (count == 1)
            {
                for (int i = 0; i < width; i++)
                    columns[i] = samples[0];
                return columns;
            }
            if (count <= width)
            {
                for (int x = 0; x < width; x++)
                {
                    double position = width == 1 ? 0.0 : (double)x * (count - 1) / (width - 1);
                    int i = (int)Math.Floor(position);
                    int j = Math.Min(count - 1, i + 1);
                    float t = (float)(position - i);
                    float a = samples[i];
                    float b = samples[j];
                    columns[x] = float.IsNaN(a) || float.IsNaN(b) ? float.NaN : a + ((b - a) * t);
                }
                return columns;
            }

            for (int x = 0; x < width; x++)
            {
                int first = (int)((long)x * count / width);
                int last = Math.Max(first, (int)((long)(x + 1) * count / width) - 1);
                if (sum)
                {
                    float total = 0f;
                    bool any = false;
                    for (int i = first; i <= last && i < count; i++)
                    {
                        if (float.IsNaN(samples[i]))
                            continue;
                        total += samples[i];
                        any = true;
                    }
                    columns[x] = any ? total : float.NaN;
                }
                else
                {
                    float value = float.NaN;
                    for (int i = first; i <= last && i < count; i++)
                        if (!float.IsNaN(samples[i]))
                            value = samples[i];
                    columns[x] = value;
                }
            }
            return columns;
        }

        private static float[] Bucket(float[] samples, int group, int slots)
        {
            var values = new float[slots];
            for (int slot = 0; slot < slots; slot++)
            {
                int first = slot * group;
                int last = Math.Min(samples.Length - 1, first + group - 1);
                if (first >= samples.Length)
                {
                    values[slot] = float.NaN;
                    continue;
                }
                float total = 0f;
                bool any = false;
                for (int i = first; i <= last; i++)
                {
                    if (float.IsNaN(samples[i]))
                        continue;
                    total += samples[i];
                    any = true;
                }
                values[slot] = any ? total : float.NaN;
            }
            return values;
        }

        private static float ChooseStep(float range, int targetTicks)
        {
            if (range <= 0f)
                return 1f;
            float raw = range / Math.Max(2, targetTicks);
            float magnitude = (float)Math.Pow(10, Math.Floor(Math.Log10(raw)));
            foreach (var mantissa in Mantissas)
            {
                float step = mantissa * magnitude;
                if (step >= raw)
                    return step;
            }
            return 10f * magnitude;
        }

        private static float MinorStep(float step)
        {
            float magnitude = (float)Math.Pow(10, Math.Floor(Math.Log10(step) + 1e-6));
            float mantissa = step / magnitude;
            bool twos = Math.Abs(mantissa - 2f) < 0.01f;
            return step / (twos ? 4f : 5f);
        }

        private static List<float> TickValues(float low, float high, float step)
        {
            var ticks = new List<float>();
            double first = Math.Ceiling((low / step) - 1e-3);
            double last = Math.Floor((high / step) + 1e-3);
            if (last - first > 64)
                return ticks;
            for (double i = first; i <= last; i += 1.0)
                ticks.Add((float)(i * step));
            return ticks;
        }

        private string FormatTick(float value, float step)
        {
            if (Math.Abs(value) < 0.00005f)
                value = 0f;
            if (AxisLabel != null)
                return AxisLabel(value);

            float magnitude = Math.Abs(value);
            int decimals = step >= 1f ? 0 : (int)Math.Ceiling(-Math.Log10(step) - 1e-6);
            decimals = Math.Clamp(decimals, 0, 4);
            return decimals == 0 && magnitude >= 10000f
                ? value.ToString("N0")
                : value.ToString("F" + decimals);
        }

        private void Range(out float low, out float high)
        {
            low = float.PositiveInfinity;
            high = float.NegativeInfinity;

            if (Kind == ChartKind.StackedArea || Kind == ChartKind.StackedColumn)
            {
                int count = 0;
                foreach (var series in Series)
                    count = Math.Max(count, series.Samples.Length);

                bool any = false;
                for (int i = 0; i < count; i++)
                {
                    float total = 0f;
                    foreach (var series in Series)
                    {
                        if (i >= series.Samples.Length)
                            continue;
                        var sample = series.Samples[i];
                        if (float.IsNaN(sample))
                        {
                            total = float.NaN;
                            break;
                        }
                        total += sample;
                    }
                    if (float.IsNaN(total))
                        continue;
                    any = true;
                    low = Math.Min(low, total);
                    high = Math.Max(high, total);
                }
                if (!any)
                {
                    low = 0f;
                    high = 1f;
                }
                low = Math.Min(low, 0f);
            }
            else
            {
                foreach (var series in Series)
                    foreach (var sample in series.Samples)
                    {
                        if (float.IsNaN(sample))
                            continue;
                        low = Math.Min(low, sample);
                        high = Math.Max(high, sample);
                    }
                if (!float.IsFinite(low))
                {
                    low = 0f;
                    high = 1f;
                }
                if (Kind == ChartKind.Column)
                    low = Math.Min(low, 0f);
            }

            float span = high - low;
            if (span <= 0f)
                high += Math.Abs(low) > 0f ? Math.Abs(low) * 0.5f : 1f;
            else
            {
                high += span * 0.08f;
                if (Kind == ChartKind.Line)
                    low -= span * 0.04f;
            }

            if (!float.IsNaN(AxisMin))
                low = AxisMin;
            if (!float.IsNaN(AxisMax))
                high = AxisMax;
            if (high - low < 1e-4f)
                high = low + 1f;
        }

        private void RefreshRange(bool animate)
        {
            if (!HasAxes)
                return;
            Range(out float low, out float high);
            _targetMin = low;
            _targetMax = high;
            if (animate)
            {
                StartScaleTween();
                return;
            }
            _scaleTween = false;
            _shownMin = low;
            _shownMax = high;
        }

        private Color AxisColorOf(int index)
        {
            var series = Series[index];
            return series.ColorSet ? series.Color : Palette[index % Palette.Length];
        }

        private Color ColorOf(ChartSeries series)
        {
            if (series.ColorSet)
                return series.Color;
            var scale = ColorScale ?? DefaultColorScale;
            return scale == null ? series.Color : scale(Fraction(series.Shown));
        }

        private float Fraction(float value)
            => Math.Clamp((value - _shownMin) / (_shownMax - _shownMin), 0f, 1f);

        private void Assign(float[] values, bool animate)
        {
            while (Series.Count < values.Length)
            {
                Series.Add(new ChartSeries());
                _from = new float[Series.Count];
            }

            int count = Math.Min(values.Length, Series.Count);
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                if (Series[i].Value == values[i])
                    continue;
                Series[i].Value = values[i];
                changed = true;
            }

            if (!animate)
            {
                _valuesTween = false;
                foreach (var series in Series)
                    series.Shown = series.Value;
                return;
            }
            if (changed)
                StartValuesTween();
        }

        private void AssignSamples(float[][] rows, bool animate)
        {
            while (Series.Count < rows.Length)
            {
                Series.Add(new ChartSeries());
                _from = new float[Series.Count];
            }

            int count = Math.Min(rows.Length, Series.Count);
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                var series = Series[i];
                var samples = rows[i] ?? Array.Empty<float>();

                if (series.ShownSamples.Length != samples.Length)
                {
                    var shown = new float[samples.Length];
                    if (!animate)
                        Array.Copy(samples, shown, samples.Length);
                    else
                        for (int j = 0; j < samples.Length && j < series.ShownSamples.Length; j++)
                            shown[j] = series.ShownSamples[j];
                    series.ShownSamples = shown;
                }

                if (!Same(series.Samples, samples))
                {
                    series.Samples = samples;
                    series.FromSamples = (float[])series.ShownSamples.Clone();
                    changed = true;
                }
            }

            if (!animate)
            {
                _valuesTween = false;
                foreach (var series in Series)
                {
                    if (series.ShownSamples.Length == series.Samples.Length)
                        Array.Copy(series.Samples, series.ShownSamples, series.Samples.Length);
                    else
                        series.ShownSamples = (float[])series.Samples.Clone();
                }
                RefreshRange(animate: false);
                return;
            }

            if (!changed)
                return;
            StartValuesTween();
            RefreshRange(animate: true);
        }

        private static bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i]))
                    return false;
            return true;
        }

        private void StartValuesTween()
        {
            if (_from.Length != Series.Count)
                _from = new float[Series.Count];
            for (int i = 0; i < Series.Count; i++)
                _from[i] = Series[i].Shown;

            double now = NowMs();
            _valuesStart = now;
            _valuesEnd = now + Math.Max(1, AnimateMillis);
            _valuesTween = true;
        }

        private void StartScaleTween()
        {
            _scaleFrom = _shownMax;
            _minFrom = _shownMin;
            double now = NowMs();
            _scaleStart = now;
            _scaleEnd = now + Math.Max(1, AnimateMillis);
            _scaleTween = true;
        }

        private void Advance()
        {
            double now = NowMs();

            if (_scaleTween)
            {
                float progress = Progress(_scaleStart, _scaleEnd, now);
                _shownMax = _scaleFrom + ((_targetMax - _scaleFrom) * progress);
                _shownMin = _minFrom + ((_targetMin - _minFrom) * progress);
                if (progress >= 1f)
                {
                    _shownMax = _targetMax;
                    _shownMin = _targetMin;
                    _scaleTween = false;
                }
            }

            if (_valuesTween)
            {
                float progress = Progress(_valuesStart, _valuesEnd, now);
                for (int i = 0; i < Series.Count; i++)
                {
                    var series = Series[i];
                    series.Shown = _from[i] + ((series.Value - _from[i]) * progress);

                    var from = series.FromSamples;
                    var target = series.Samples;
                    var shown = series.ShownSamples;
                    int count = Math.Min(shown.Length, Math.Min(from.Length, target.Length));
                    for (int j = 0; j < count; j++)
                        shown[j] = from[j] + ((target[j] - from[j]) * progress);
                }
                if (progress >= 1f)
                {
                    foreach (var series in Series)
                    {
                        series.Shown = series.Value;
                        if (series.ShownSamples.Length == series.Samples.Length)
                            Array.Copy(series.Samples, series.ShownSamples, series.Samples.Length);
                    }
                    _valuesTween = false;
                }
            }
        }

        private static float Progress(double start, double end, double now)
            => (float)Math.Clamp((now - start) / Math.Max(1.0, end - start), 0.0, 1.0);

        private static double NowMs()
            => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    }
}
