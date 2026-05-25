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
        public double FilletRadiusCm { get; set; }
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
            double filletRadiusMm = Math.Min(
                pitchMm * 0.10,
                Math.Min(heightMm, topWidthMm) * 0.20);

            // Keep the profile conservative and inside the available diameter envelope.
            double maxProfileWidthMm = Math.Max(0.0, nominalDiameterMm * 0.85);
            baseWidthMm = Clamp(baseWidthMm, pitchMm * 0.25, maxProfileWidthMm);
            topWidthMm = Clamp(topWidthMm, pitchMm * 0.15, baseWidthMm * 0.90);
            heightMm = Clamp(heightMm, pitchMm * 0.20, Math.Max(0.1, nominalDiameterMm * 0.20));
            filletRadiusMm = Clamp(filletRadiusMm, 0.0, Math.Min(Math.Min(baseWidthMm, topWidthMm), heightMm) * 0.20);

            return new PrintThreadPreset
            {
                Name = presetName,
                BaseWidthCm = baseWidthMm * 0.1,
                TopWidthCm = topWidthMm * 0.1,
                HeightCm = heightMm * 0.1,
                PitchCm = pitchMm * 0.1,
                FilletRadiusCm = filletRadiusMm * 0.1
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
                double topRadius = centerRadius;
                double baseRadius = context.IsInteriorFace
                    ? centerRadius + preset.HeightCm
                    : centerRadius - preset.HeightCm;

                if (baseRadius <= 0.0 || topRadius <= 0.0)
                {
                    errorMessage = "Computed profile radius is invalid.";
                    tx.Abort();
                    return false;
                }

                double effectiveFilletRadius = GetEffectiveFilletRadiusCm(preset);
                bool clockwise = !context.ThreadInfo.RightHanded;
                double leadInLength = Math.Min(
                    Math.Max(preset.PitchCm, context.PitchCm),
                    Math.Max(context.UsefulLengthCm * 0.25, preset.PitchCm));
                double leadInTaper = GetLeadInTaperRadians(
                    preset,
                    leadInLength,
                    context.IsInteriorFace);
                double mainHeight = Math.Max(preset.PitchCm, context.UsefulLengthCm + (2.0 * preset.PitchCm) - leadInLength);

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Print section params: pitch={0}cm baseWidth={1}cm topWidth={2}cm height={3}cm filletRadius={4}cm centerRadius={5}cm baseRadius={6}cm topRadius={7}cm leadInLength={8}cm leadInTaper={9}rad mainHeight={10}cm clockwise={11} interior={12}",
                    preset.PitchCm,
                    preset.BaseWidthCm,
                    preset.TopWidthCm,
                    preset.HeightCm,
                    preset.FilletRadiusCm,
                    centerRadius,
                    baseRadius,
                    topRadius,
                    leadInLength,
                    leadInTaper,
                    mainHeight,
                    clockwise,
                    context.IsInteriorFace));

                Point leadInBasePoint = basePoint;
                Point mainBasePoint = OffsetPoint(basePoint, threadAxis, leadInLength);

                CoilFeature leadInCoil;
                if (!TryCreateCoilSection(
                    doc,
                    leadInBasePoint,
                    threadAxis,
                    radialAxis,
                    preset,
                    baseRadius,
                    topRadius,
                    context.IsInteriorFace,
                    effectiveFilletRadius,
                    leadInLength,
                    leadInTaper,
                    clockwise,
                    out leadInCoil))
                {
                    DebugLog.WriteLine("Lead-in ramp creation failed; continuing with the main thread only.");
                }
                else
                {
                    DebugLog.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Lead-in ramp created length={0} taper={1} interior={2}",
                        leadInLength,
                        leadInTaper,
                        context.IsInteriorFace));
                }

                CoilFeature coil;
                if (!TryCreateCoilSection(
                    doc,
                    mainBasePoint,
                    threadAxis,
                    radialAxis,
                    preset,
                    baseRadius,
                    topRadius,
                    context.IsInteriorFace,
                    effectiveFilletRadius,
                    mainHeight,
                    0.0,
                    clockwise,
                    out coil))
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
            double baseRadius,
            double topRadius,
            bool isInteriorFace)
        {
            ObjectCollection segments = _Application.TransientObjects.CreateObjectCollection();

            // External modeled threads are created by cutting a groove, so the sketch
            // must be the complement of the visible thread profile.
            double cutBaseWidth = isInteriorFace ? preset.BaseWidthCm : preset.TopWidthCm;
            double cutTopWidth = isInteriorFace ? preset.TopWidthCm : preset.BaseWidthCm;
            double height = preset.HeightCm;

            double maxWidth = Math.Max(cutBaseWidth, cutTopWidth);
            double baseInset = (maxWidth - cutBaseWidth) * 0.5;
            double topInset = (maxWidth - cutTopWidth) * 0.5;

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Trapezoid profile: visibleBaseWidth={0}cm visibleTopWidth={1}cm cutBaseWidth={2}cm cutTopWidth={3}cm height={4}cm baseRadius={5}cm topRadius={6}cm interior={7}",
                preset.BaseWidthCm,
                preset.TopWidthCm,
                cutBaseWidth,
                cutTopWidth,
                height,
                baseRadius,
                topRadius,
                isInteriorFace));

            Point2d p1 = _Tg.CreatePoint2d(baseInset, baseRadius);
            Point2d p2 = _Tg.CreatePoint2d(baseInset + cutBaseWidth, baseRadius);
            Point2d p3 = _Tg.CreatePoint2d(topInset + cutTopWidth, topRadius);
            Point2d p4 = _Tg.CreatePoint2d(topInset, topRadius);

            double requestedFilletRadius = GetEffectiveFilletRadiusCm(preset);
            double safeFilletRadius = requestedFilletRadius > 0.0
                ? GetMaxRoundedTrapezoidRadiusCm(p1, p2, p3, p4)
                : 0.0;

            if (requestedFilletRadius > 0.0)
            {
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Trapezoid fillet request: requested={0}cm safeMax={1}cm",
                    requestedFilletRadius,
                    safeFilletRadius));
            }

            if (requestedFilletRadius > 0.0 && safeFilletRadius > 0.0 && requestedFilletRadius <= safeFilletRadius)
            {
                if (TryCreateRoundedCrestProfile(
                    sketch,
                    p1,
                    p2,
                    p3,
                    p4,
                    requestedFilletRadius,
                    segments))
                {
                    DebugLog.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Rounded crest trapezoid profile created: radius={0}cm",
                        requestedFilletRadius));
                }
                else
                {
                    DebugLog.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Rounded crest trapezoid disabled after geometry build failure: requested={0}cm",
                        requestedFilletRadius));
                    BuildSharpTrapezoidProfile(sketch, p1, p2, p3, p4, segments);
                }
            }
            else
            {
                if (requestedFilletRadius > 0.0)
                {
                    DebugLog.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Trapezoid fillet suppressed: requested={0}cm safeMax={1}cm",
                        requestedFilletRadius,
                        safeFilletRadius));
                }

                BuildSharpTrapezoidProfile(sketch, p1, p2, p3, p4, segments);
            }

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Trapezoid points: p1=({0},{1}) p2=({2},{3}) p3=({4},{5}) p4=({6},{7}) segmentCount={8}",
                p1.X,
                p1.Y,
                p2.X,
                p2.Y,
                p3.X,
                p3.Y,
                p4.X,
                p4.Y,
                segments.Count));

            return segments;
        }

        private static void BuildSharpTrapezoidProfile(
            PlanarSketch sketch,
            Point2d p1,
            Point2d p2,
            Point2d p3,
            Point2d p4,
            ObjectCollection segments)
        {
            SketchPoint sp1 = sketch.SketchPoints.Add(p1, false);
            SketchPoint sp2 = sketch.SketchPoints.Add(p2, false);
            SketchPoint sp3 = sketch.SketchPoints.Add(p3, false);
            SketchPoint sp4 = sketch.SketchPoints.Add(p4, false);

            segments.Add(sketch.SketchLines.AddByTwoPoints(sp1, sp2));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp2, sp3));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp3, sp4));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp4, sp1));
        }

        private static bool TryCreateRoundedCrestProfile(
            PlanarSketch sketch,
            Point2d p1,
            Point2d p2,
            Point2d p3,
            Point2d p4,
            double filletRadius,
            ObjectCollection segments)
        {
            RoundedCorner crestRight;
            RoundedCorner crestLeft;

            if (!TryBuildRoundedCorner(p2, p3, p4, filletRadius, out crestRight) ||
                !TryBuildRoundedCorner(p3, p4, p1, filletRadius, out crestLeft))
            {
                return false;
            }

            SketchPoint sp1 = sketch.SketchPoints.Add(p1, false);
            SketchPoint sp2 = sketch.SketchPoints.Add(p2, false);
            SketchPoint sp3 = sketch.SketchPoints.Add(p3, false);
            SketchPoint sp4 = sketch.SketchPoints.Add(p4, false);

            SketchPoint crestRightIn = sketch.SketchPoints.Add(crestRight.InPoint, false);
            SketchPoint crestRightOut = sketch.SketchPoints.Add(crestRight.OutPoint, false);
            SketchPoint crestLeftIn = sketch.SketchPoints.Add(crestLeft.InPoint, false);
            SketchPoint crestLeftOut = sketch.SketchPoints.Add(crestLeft.OutPoint, false);

            segments.Add(sketch.SketchLines.AddByTwoPoints(sp1, sp2));
            segments.Add(sketch.SketchLines.AddByTwoPoints(sp2, crestRightIn));
            segments.Add(sketch.SketchArcs.AddByThreePoints(crestRightIn, crestRight.MidPoint, crestRightOut));
            segments.Add(sketch.SketchLines.AddByTwoPoints(crestRightOut, crestLeftIn));
            segments.Add(sketch.SketchArcs.AddByThreePoints(crestLeftIn, crestLeft.MidPoint, crestLeftOut));
            segments.Add(sketch.SketchLines.AddByTwoPoints(crestLeftOut, sp1));

            return true;
        }

        private static bool TryCreateCoilSection(
            PartDocument doc,
            Point basePoint,
            UnitVector threadAxis,
            UnitVector radialAxis,
            PrintThreadPreset preset,
            double baseRadius,
            double topRadius,
            bool isInteriorFace,
            double filletRadius,
            double coilHeight,
            double taper,
            bool clockwise,
            out CoilFeature coil)
        {
            coil = null;

            try
            {
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

                ObjectCollection profileSegments = DrawTrapezoidProfile(
                    sketch,
                    preset,
                    baseRadius,
                    topRadius,
                    isInteriorFace);

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "TryCreateCoilSection: basePoint=({0},{1},{2}) coilHeight={3} taper={4} filletRadius={5} clockwise={6} baseRadius={7} topRadius={8} interior={9} profileSegments={10}",
                    basePoint.X,
                    basePoint.Y,
                    basePoint.Z,
                    coilHeight,
                    taper,
                    filletRadius,
                    clockwise,
                    baseRadius,
                    topRadius,
                    isInteriorFace,
                    profileSegments == null ? 0 : profileSegments.Count));

                Profile profile = sketch.Profiles.AddForSolid(false, profileSegments, null);

                coil = doc.ComponentDefinition.Features.CoilFeatures.AddByPitchAndHeight(
                    profile,
                    sketchAxis,
                    preset.PitchCm,
                    coilHeight,
                    PartFeatureOperationEnum.kCutOperation,
                    false,
                    clockwise,
                    taper,
                    false,
                    0.0,
                    0.0,
                    false,
                    0.0,
                    0.0);

                return (coil != null && coil.HealthStatus == HealthStatusEnum.kUpToDateHealth);
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("TryCreateCoilSection failed.", ex);
                coil = null;
                return false;
            }
        }

        private static Point OffsetPoint(
            Point basePoint,
            UnitVector direction,
            double distance)
        {
            return _Tg.CreatePoint(
                basePoint.X + direction.X * distance,
                basePoint.Y + direction.Y * distance,
                basePoint.Z + direction.Z * distance);
        }

        private static double GetEffectiveFilletRadiusCm(PrintThreadPreset preset)
        {
            if (preset == null || preset.FilletRadiusCm <= 0.0)
            {
                return 0.0;
            }

            double minDimension = Math.Min(
                Math.Min(preset.BaseWidthCm, preset.TopWidthCm),
                preset.HeightCm);

            double safeRadius = minDimension * 0.20;
            if (safeRadius <= 0.0 || preset.FilletRadiusCm > safeRadius)
            {
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Trapezoid fillet suppressed: requested={0}cm safeMax={1}cm minDimension={2}cm",
                    preset.FilletRadiusCm,
                    safeRadius,
                    minDimension));
                return 0.0;
            }

            return preset.FilletRadiusCm;
        }

        private static double GetMaxRoundedTrapezoidRadiusCm(
            Point2d p1,
            Point2d p2,
            Point2d p3,
            Point2d p4)
        {
            double crestRightRadius = GetCornerMaxRadiusCm(p2, p3, p4);
            double crestLeftRadius = GetCornerMaxRadiusCm(p3, p4, p1);

            return Math.Min(crestRightRadius, crestLeftRadius);
        }

        private static double GetCornerMaxRadiusCm(
            Point2d prev,
            Point2d corner,
            Point2d next)
        {
            double prevDx = prev.X - corner.X;
            double prevDy = prev.Y - corner.Y;
            double nextDx = next.X - corner.X;
            double nextDy = next.Y - corner.Y;

            double prevLength = Math.Sqrt((prevDx * prevDx) + (prevDy * prevDy));
            double nextLength = Math.Sqrt((nextDx * nextDx) + (nextDy * nextDy));
            if (prevLength <= 0.0 || nextLength <= 0.0)
            {
                return 0.0;
            }

            double prevUx = prevDx / prevLength;
            double prevUy = prevDy / prevLength;
            double nextUx = nextDx / nextLength;
            double nextUy = nextDy / nextLength;

            double dot = (prevUx * nextUx) + (prevUy * nextUy);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));

            double angle = Math.Acos(dot);
            if (angle <= 0.0 || angle >= Math.PI)
            {
                return 0.0;
            }

            double tangentDistance = Math.Min(prevLength, nextLength) * 0.45;
            double maxRadius = tangentDistance / Math.Tan(angle * 0.5);
            return Math.Max(0.0, maxRadius);
        }

        private sealed class RoundedCorner
        {
            public Point2d InPoint { get; set; }
            public Point2d OutPoint { get; set; }
            public Point2d MidPoint { get; set; }
        }

        private static bool TryBuildRoundedCorner(
            Point2d prev,
            Point2d corner,
            Point2d next,
            double radius,
            out RoundedCorner result)
        {
            result = null;

            double prevDx = prev.X - corner.X;
            double prevDy = prev.Y - corner.Y;
            double nextDx = next.X - corner.X;
            double nextDy = next.Y - corner.Y;

            double prevLength = Math.Sqrt((prevDx * prevDx) + (prevDy * prevDy));
            double nextLength = Math.Sqrt((nextDx * nextDx) + (nextDy * nextDy));
            if (prevLength <= 0.0 || nextLength <= 0.0)
            {
                return false;
            }

            double prevUx = prevDx / prevLength;
            double prevUy = prevDy / prevLength;
            double nextUx = nextDx / nextLength;
            double nextUy = nextDy / nextLength;

            double dot = (prevUx * nextUx) + (prevUy * nextUy);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));

            double angle = Math.Acos(dot);
            if (angle <= 0.0 || angle >= Math.PI)
            {
                return false;
            }

            double tangentDistance = radius * Math.Tan(angle * 0.5);
            if (tangentDistance <= 0.0 ||
                tangentDistance >= prevLength ||
                tangentDistance >= nextLength)
            {
                return false;
            }

            double bisectorX = prevUx + nextUx;
            double bisectorY = prevUy + nextUy;
            double bisectorLength = Math.Sqrt((bisectorX * bisectorX) + (bisectorY * bisectorY));
            if (bisectorLength <= 0.0)
            {
                return false;
            }

            double centerDistance = radius / Math.Sin(angle * 0.5);
            double centerX = corner.X + ((bisectorX / bisectorLength) * centerDistance);
            double centerY = corner.Y + ((bisectorY / bisectorLength) * centerDistance);

            double cornerVectorX = corner.X - centerX;
            double cornerVectorY = corner.Y - centerY;
            double cornerVectorLength = Math.Sqrt((cornerVectorX * cornerVectorX) + (cornerVectorY * cornerVectorY));
            if (cornerVectorLength <= 0.0)
            {
                return false;
            }

            double midX = centerX + ((cornerVectorX / cornerVectorLength) * radius);
            double midY = centerY + ((cornerVectorY / cornerVectorLength) * radius);

            result = new RoundedCorner
            {
                InPoint = _Tg.CreatePoint2d(
                    corner.X + (prevUx * tangentDistance),
                    corner.Y + (prevUy * tangentDistance)),
                OutPoint = _Tg.CreatePoint2d(
                    corner.X + (nextUx * tangentDistance),
                    corner.Y + (nextUy * tangentDistance)),
                MidPoint = _Tg.CreatePoint2d(midX, midY)
            };

            return true;
        }

        private static double GetLeadInTaperRadians(
            PrintThreadPreset preset,
            double leadInLength,
            bool isInteriorFace)
        {
            double span = Math.Max(leadInLength, preset.PitchCm * 0.25);
            double rise = Math.Max(preset.HeightCm * 0.35, preset.HeightCm * 0.15);
            double taper = Math.Atan(rise / span);
            taper = Math.Max(0.08, Math.Min(0.30, taper));

            return isInteriorFace ? -taper : taper;
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
