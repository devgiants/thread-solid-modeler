////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// ThreadSolidModeler 3D Print worker
//
// The print path is intentionally separate from the ISO path. It rebuilds a modeled
// thread from the selected thread feature metadata, using conservative parameters and
// explicit geometry validation to reduce coil failures.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Inventor;
using ThreadModeler.Utilities;

namespace ThreadModeler
{
    internal sealed class PrintThreadPreset
    {
        public string Name { get; set; }
        public double BaseWidthCm { get; set; }
        public double TopWidthCm { get; set; }
        public double HeightCm { get; set; }
        public double PitchCm { get; set; }
    }

    internal sealed class PrintThreadContext
    {
        public ThreadFeature ThreadFeature { get; set; }
        public PartFeature Feature { get; set; }
        public ThreadInfo ThreadInfo { get; set; }
        public Face ThreadedFace { get; set; }
        public bool IsInteriorFace { get; set; }
        public double NominalDiameterCm { get; set; }
        public double UsefulLengthCm { get; set; }
        public string NominalLabel { get; set; }
        public double PitchCm { get; set; }
    }

    internal static class PrintThreadWorker
    {
        private static Inventor.Application _Application;
        private static TransientGeometry _Tg;

        private static readonly double[] MetricNominalSizesMm =
        {
            4.0, 5.0, 6.0, 8.0, 10.0, 12.0, 16.0, 20.0
        };

        private static readonly double[] MetricCoarsePitchesMm =
        {
            0.7, 0.8, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5
        };

        public static void Initialize(Inventor.Application Application)
        {
            _Application = Application;
            _Tg = _Application.TransientGeometry;
        }

        public static bool TryBuildContext(
            object obj,
            PartDocument doc,
            out PrintThreadContext context,
            out string errorMessage)
        {
            context = null;
            errorMessage = string.Empty;

            ThreadFeature thread = obj as ThreadFeature;
            PartFeature feature = obj as PartFeature;

            if (thread == null || feature == null)
            {
                thread = ResolveThreadFeatureProxy(obj);
                feature = thread as PartFeature;
            }

            if (thread == null || feature == null)
            {
                errorMessage = "Select a ThreadFeature.";
                return false;
            }

            if (feature.Suppressed)
            {
                errorMessage = "Selected thread feature is suppressed.";
                return false;
            }

            Face threadedFace = null;
            try
            {
                threadedFace = thread.ThreadedFace[1];
            }
            catch
            {
                threadedFace = null;
            }

            if (threadedFace == null)
            {
                errorMessage = "Selected thread has no threaded face.";
                return false;
            }

            string iMateName;
            if (Toolkit.HasiMate(threadedFace, out iMateName))
            {
                errorMessage = "Selected face has iMate " + iMateName + ". Remove it before generating print geometry.";
                return false;
            }

            ThreadInfo threadInfo = thread.ThreadInfo;
            if (threadInfo == null)
            {
                errorMessage = "Selected thread has no thread information.";
                return false;
            }

            double pitchCm = ThreadWorker.GetThreadPitch(threadInfo);
            if (pitchCm < ThreadWorker.ThresholdPitchCm)
            {
                errorMessage = "Selected thread pitch is too small.";
                return false;
            }

            double nominalDiameterCm;
            string nominalLabel;
            if (!TryGetNominalDiameterCm(threadInfo, out nominalDiameterCm, out nominalLabel))
            {
                errorMessage = "Could not detect the nominal diameter for the selected thread.";
                return false;
            }

            double usefulLengthCm = GetUsefulLengthCm(threadInfo);
            if (usefulLengthCm <= 0.0)
            {
                errorMessage = "Selected thread length is invalid.";
                return false;
            }

            context = new PrintThreadContext
            {
                ThreadFeature = thread,
                Feature = feature,
                ThreadInfo = threadInfo,
                ThreadedFace = threadedFace,
                IsInteriorFace = IsInteriorFaceSafe(threadedFace),
                NominalDiameterCm = nominalDiameterCm,
                UsefulLengthCm = usefulLengthCm,
                NominalLabel = nominalLabel,
                PitchCm = pitchCm
            };

            return true;
        }

        public static PrintThreadPreset BuildPreset(PrintThreadContext context)
        {
            double nominalDiameterMm = context.NominalDiameterCm * 10.0;
            double pitchMm;
            string presetName;

            if (!TryGetMetricPreset(nominalDiameterMm, out pitchMm, out presetName))
            {
                pitchMm = context.PitchCm * 10.0;
                presetName = context.NominalLabel + " fallback";
            }

            double baseWidthMm = pitchMm * 0.70;
            double topWidthMm = pitchMm * 0.35;
            double heightMm = pitchMm * 0.50;

            // Keep the profile conservative and inside the available diameter envelope.
            double maxProfileWidthMm = Math.Max(0.0, nominalDiameterMm * 0.85);
            baseWidthMm = Clamp(baseWidthMm, pitchMm * 0.25, maxProfileWidthMm);
            topWidthMm = Clamp(topWidthMm, pitchMm * 0.15, baseWidthMm * 0.90);
            heightMm = Clamp(heightMm, pitchMm * 0.20, Math.Max(0.1, nominalDiameterMm * 0.20));

            return new PrintThreadPreset
            {
                Name = presetName,
                BaseWidthCm = baseWidthMm * 0.1,
                TopWidthCm = topWidthMm * 0.1,
                HeightCm = heightMm * 0.1,
                PitchCm = pitchMm * 0.1
            };
        }

        public static bool ModelizeThreadPrint(
            PartDocument doc,
            PrintThreadContext context,
            PrintThreadPreset preset,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            if (context == null || preset == null)
            {
                errorMessage = "Missing print context.";
                return false;
            }

            if (!ValidatePreset(context, preset, out errorMessage))
            {
                return false;
            }

            Transaction tx = _Application.TransactionManager.StartTransaction(
                doc as _Document,
                "Modelizing Print Thread " + context.Feature.Name);

            try
            {
                Vector threadDirection = context.ThreadInfo.ThreadDirection;
                UnitVector threadAxis = threadDirection.AsUnitVector();
                UnitVector radialAxis = Toolkit.GetOrthoVector(threadAxis);
                Point basePoint = context.ThreadInfo.ThreadBasePoints[1] as Point;

                if (basePoint == null)
                {
                    errorMessage = "Selected thread has no base point.";
                    tx.Abort();
                    return false;
                }

                double centerRadius = GetReferenceRadiusCm(context);
                double radialOffset = Math.Max(preset.HeightCm * 0.50, centerRadius - preset.HeightCm * 0.50);
                if (radialOffset <= 0.0)
                {
                    errorMessage = "Computed profile offset is invalid.";
                    tx.Abort();
                    return false;
                }

                WorkAxis sketchAxis = doc.ComponentDefinition.WorkAxes.AddFixed(
                    basePoint,
                    threadAxis,
                    true);

                WorkAxis radialWorkAxis = doc.ComponentDefinition.WorkAxes.AddFixed(
                    basePoint,
                    radialAxis,
                    true);

                WorkPlane sketchPlane = doc.ComponentDefinition.WorkPlanes.AddByTwoLines(
                    sketchAxis,
                    radialWorkAxis,
                    true);

                WorkPoint sketchOrigin = doc.ComponentDefinition.WorkPoints.AddFixed(
                    basePoint,
                    true);

                PlanarSketch sketch = doc.ComponentDefinition.Sketches.AddWithOrientation(
                    sketchPlane,
                    sketchAxis,
                    true,
                    true,
                    sketchOrigin,
                    false);

                ObjectCollection profileSegments = DrawTrapezoidProfile(sketch, preset, radialOffset);

                Profile profile = sketch.Profiles.AddForSolid(false, profileSegments, null);

                bool clockwise = !context.ThreadInfo.RightHanded;
                double coilHeight = context.UsefulLengthCm + (2.0 * preset.PitchCm);

                CoilFeature coil = doc.ComponentDefinition.Features.CoilFeatures.AddByPitchAndHeight(
                    profile,
                    sketchAxis,
                    preset.PitchCm,
                    coilHeight,
                    PartFeatureOperationEnum.kCutOperation,
                    false,
                    clockwise,
                    0.0,
                    false,
                    0.0,
                    0.0,
                    false,
                    0.0,
                    0.0);

                if (coil == null || coil.HealthStatus != HealthStatusEnum.kUpToDateHealth)
                {
                    errorMessage = "Inventor returned an unhealthy coil feature.";
                    tx.Abort();
                    return false;
                }

                context.Feature.Suppressed = true;

                tx.End();
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("ModelizeThreadPrint failed for " + context.Feature.Name, ex);
                errorMessage = "Failed to generate 3D print thread.";
                try
                {
                    tx.Abort();
                }
                catch
                {
                }

                return false;
            }
        }

        private static ObjectCollection DrawTrapezoidProfile(
            PlanarSketch sketch,
            PrintThreadPreset preset,
            double radialOffset)
        {
            ObjectCollection segments = _Application.TransientObjects.CreateObjectCollection();

            double baseWidth = preset.BaseWidthCm;
            double topWidth = preset.TopWidthCm;
            double height = preset.HeightCm;

            double taper = Math.Max(0.0, (baseWidth - topWidth) * 0.5);

            Point2d p1 = _Tg.CreatePoint2d(0.0, radialOffset);
            Point2d p2 = _Tg.CreatePoint2d(baseWidth, radialOffset);
            Point2d p3 = _Tg.CreatePoint2d(baseWidth - taper, radialOffset + height);
            Point2d p4 = _Tg.CreatePoint2d(taper, radialOffset + height);

            SketchPoint sp1 = sketch.SketchPoints.Add(p1, false);
            SketchPoint sp2 = sketch.SketchPoints.Add(p2, false);
            SketchPoint sp3 = sketch.SketchPoints.Add(p3, false);
            SketchPoint sp4 = sketch.SketchPoints.Add(p4, false);

            segments.Add(sketch.SketchLines.AddByTwoPoints(sp1, sp2));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp2, sp3));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp3, sp4));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp4, sp1));

            return segments;
        }

        private static bool ValidatePreset(
            PrintThreadContext context,
            PrintThreadPreset preset,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            if (preset.PitchCm <= 0.0 ||
                preset.BaseWidthCm <= 0.0 ||
                preset.TopWidthCm <= 0.0 ||
                preset.HeightCm <= 0.0)
            {
                errorMessage = "Print preset values must be strictly positive.";
                return false;
            }

            if (preset.TopWidthCm >= preset.BaseWidthCm)
            {
                errorMessage = "Top width must be smaller than base width.";
                return false;
            }

            if (preset.PitchCm < ThreadWorker.ThresholdPitchCm)
            {
                errorMessage = "Pitch is too small.";
                return false;
            }

            double nominalRadius = context.NominalDiameterCm * 0.5;
            if (nominalRadius <= preset.BaseWidthCm * 0.5)
            {
                errorMessage = "Nominal diameter is too small for the requested profile.";
                return false;
            }

            if (preset.PitchCm < preset.BaseWidthCm * 0.75)
            {
                errorMessage = "Pitch is too small compared to the profile width.";
                return false;
            }

            return true;
        }

        private static bool TryGetMetricPreset(
            double nominalDiameterMm,
            out double pitchMm,
            out string presetName)
        {
            pitchMm = 0.0;
            presetName = string.Empty;

            for (int i = 0; i < MetricNominalSizesMm.Length; i++)
            {
                if (Math.Abs(MetricNominalSizesMm[i] - nominalDiameterMm) <= 0.35)
                {
                    pitchMm = MetricCoarsePitchesMm[i];
                    presetName = "M" + MetricNominalSizesMm[i].ToString("0.#", CultureInfo.InvariantCulture);
                    return true;
                }
            }

            int nearestIndex = -1;
            double nearestDistance = double.MaxValue;
            for (int i = 0; i < MetricNominalSizesMm.Length; i++)
            {
                double distance = Math.Abs(MetricNominalSizesMm[i] - nominalDiameterMm);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestIndex = i;
                }
            }

            if (nearestIndex >= 0 && nearestDistance <= 1.50)
            {
                pitchMm = MetricCoarsePitchesMm[nearestIndex];
                presetName = "M" + MetricNominalSizesMm[nearestIndex].ToString("0.#", CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        private static bool TryGetNominalDiameterCm(
            ThreadInfo threadInfo,
            out double nominalDiameterCm,
            out string nominalLabel)
        {
            nominalDiameterCm = 0.0;
            nominalLabel = string.Empty;

            StandardThreadInfo standard = threadInfo as StandardThreadInfo;
            if (standard != null)
            {
                string label = standard.NominalSize;
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = standard.ThreadDesignation;
                }

                nominalLabel = string.IsNullOrWhiteSpace(label) ? "Unknown" : label;

                if (TryParseNominalDiameter(label, IsMetricThread(threadInfo), out nominalDiameterCm))
                {
                    return true;
                }
            }

            string fallback = threadInfo.ThreadDesignation;
            nominalLabel = string.IsNullOrWhiteSpace(fallback) ? "Unknown" : fallback;
            return TryParseNominalDiameter(fallback, IsMetricThread(threadInfo), out nominalDiameterCm);
        }

        private static bool TryParseNominalDiameter(
            string label,
            bool metric,
            out double nominalDiameterCm)
        {
            nominalDiameterCm = 0.0;

            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            if (label.IndexOf('#') >= 0)
            {
                Match gaugeMatch = Regex.Match(label, @"#(?<gauge>\d+)");
                if (gaugeMatch.Success)
                {
                    int gauge = int.Parse(gaugeMatch.Groups["gauge"].Value, CultureInfo.InvariantCulture);
                    double gaugeInch;
                    if (TryGetGaugeNominalInch(gauge, out gaugeInch))
                    {
                        nominalDiameterCm = gaugeInch * 2.54;
                        return true;
                    }
                }
            }

            Match fractionMatch = Regex.Match(label, @"(?<num>\d+)\s*/\s*(?<den>\d+)");
            if (fractionMatch.Success)
            {
                double numerator = double.Parse(fractionMatch.Groups["num"].Value, CultureInfo.InvariantCulture);
                double denominator = double.Parse(fractionMatch.Groups["den"].Value, CultureInfo.InvariantCulture);
                if (denominator > 0.0)
                {
                    nominalDiameterCm = (numerator / denominator) * 2.54;
                    return true;
                }
            }

            Match decimalMatch = Regex.Match(label, @"(?<value>\d+(?:[.,]\d+)?)");
            if (!decimalMatch.Success)
            {
                return false;
            }

            string valueText = decimalMatch.Groups["value"].Value.Replace(',', '.');
            double value;
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            nominalDiameterCm = metric ? value * 0.1 : value * 2.54;
            return true;
        }

        private static bool TryGetGaugeNominalInch(int gauge, out double inch)
        {
            switch (gauge)
            {
                case 0:
                    inch = 0.0600;
                    return true;
                case 1:
                    inch = 0.0730;
                    return true;
                case 2:
                    inch = 0.0860;
                    return true;
                case 3:
                    inch = 0.0990;
                    return true;
                case 4:
                    inch = 0.1120;
                    return true;
                case 5:
                    inch = 0.1250;
                    return true;
                case 6:
                    inch = 0.1380;
                    return true;
                case 8:
                    inch = 0.1640;
                    return true;
                case 10:
                    inch = 0.1900;
                    return true;
                case 12:
                    inch = 0.2160;
                    return true;
                default:
                    inch = 0.0;
                    return false;
            }
        }

        private static bool IsMetricThread(ThreadInfo threadInfo)
        {
            object metricValue = Toolkit.GetProperty(threadInfo, "Metric");
            if (metricValue is bool)
            {
                return (bool)metricValue;
            }

            return true;
        }

        private static double GetUsefulLengthCm(ThreadInfo threadInfo)
        {
            Vector direction = threadInfo.ThreadDirection;
            if (direction == null)
            {
                return 0.0;
            }

            return direction.Length;
        }

        private static bool IsInteriorFaceSafe(Face face)
        {
            try
            {
                return Toolkit.IsInteriorFace(face);
            }
            catch
            {
                return false;
            }
        }

        private static double GetReferenceRadiusCm(PrintThreadContext context)
        {
            object radius = Toolkit.GetProperty(context.ThreadedFace.Geometry, "Radius");
            if (radius is double)
            {
                double radiusValue = (double)radius;
                if (radiusValue > 0.0)
                {
                    return radiusValue;
                }
            }

            return context.NominalDiameterCm * 0.5;
        }

        private static ThreadFeature ResolveThreadFeatureProxy(object obj)
        {
            try
            {
                object nativeObject = obj.GetType().InvokeMember(
                    "NativeObject",
                    System.Reflection.BindingFlags.GetProperty,
                    null,
                    obj,
                    null);

                return nativeObject as ThreadFeature;
            }
            catch
            {
                return null;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (max < min)
            {
                double tmp = min;
                min = max;
                max = tmp;
            }

            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
