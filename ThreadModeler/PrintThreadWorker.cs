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
        public double ClearanceCm { get; set; }
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
                PitchCm = pitchMm * 0.1,
                ClearanceCm = 0.0
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

            PrintThreadPreset effectivePreset = BuildEffectivePreset(preset);
            double radialClearance = GetRadialClearanceCm(preset);

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
                double baseRadius = context.IsInteriorFace
                    ? centerRadius
                    : centerRadius - preset.HeightCm - radialClearance;
                double topRadius = context.IsInteriorFace
                    ? centerRadius - effectivePreset.HeightCm
                    : centerRadius - radialClearance;

                if (baseRadius <= 0.0 || topRadius <= 0.0)
                {
                    errorMessage = "Computed profile radius is invalid.";
                    tx.Abort();
                    return false;
                }

                bool clockwise = !context.ThreadInfo.RightHanded;
                double leadOutLength = GetLeadOutLengthCm(context, effectivePreset);
                BalanceRampLengths(context, effectivePreset, ref leadOutLength);
                double mainHeight = context.IsInteriorFace
                    ? Math.Max(effectivePreset.PitchCm, context.UsefulLengthCm - GetInteriorProfileAxialWidthCm(effectivePreset))
                    : Math.Max(effectivePreset.PitchCm, context.UsefulLengthCm - leadOutLength);
                double startOffset = context.IsInteriorFace
                    ? 0.0
                    : GetExternalStartOffsetCm(effectivePreset);
                Point coilBasePoint = startOffset <= 0.0
                    ? basePoint
                    : OffsetPoint(basePoint, threadAxis, -startOffset);
                UnitVector coilRadialAxis = startOffset <= 0.0
                    ? radialAxis
                    : GetSectionRadialAxis(
                        radialAxis,
                        threadAxis,
                        -startOffset,
                        effectivePreset.PitchCm,
                        clockwise);
                double coilHeight = context.IsInteriorFace
                    ? mainHeight
                    : mainHeight + startOffset;

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Print clearance params: clearance={0}cm radialClearance={1}cm originalBaseWidth={2}cm originalTopWidth={3}cm effectiveBaseWidth={4}cm effectiveTopWidth={5}cm originalHeight={6}cm effectiveHeight={7}cm pitch={8}cm effectivePitch={9}cm",
                    preset.ClearanceCm,
                    radialClearance,
                    preset.BaseWidthCm,
                    preset.TopWidthCm,
                    effectivePreset.BaseWidthCm,
                    effectivePreset.TopWidthCm,
                    preset.HeightCm,
                    effectivePreset.HeightCm,
                    preset.PitchCm,
                    effectivePreset.PitchCm));

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Print section params: pitch={0}cm baseWidth={1}cm topWidth={2}cm height={3}cm centerRadius={4}cm baseRadius={5}cm topRadius={6}cm leadOutLength={7}cm mainHeight={8}cm startOffset={9}cm coilHeight={10}cm clockwise={11} interior={12}",
                    effectivePreset.PitchCm,
                    effectivePreset.BaseWidthCm,
                    effectivePreset.TopWidthCm,
                    effectivePreset.HeightCm,
                    centerRadius,
                    baseRadius,
                    topRadius,
                    leadOutLength,
                    mainHeight,
                    startOffset,
                    coilHeight,
                    clockwise,
                    context.IsInteriorFace));

                CoilFeature coil;
                if (!TryCreateCoilSection(
                    doc,
                    coilBasePoint,
                    threadAxis,
                    coilRadialAxis,
                    effectivePreset,
                    baseRadius,
                    topRadius,
                    context.IsInteriorFace,
                    coilHeight,
                    0.0,
                    clockwise,
                    out coil))
                {
                    errorMessage = "Inventor returned an unhealthy coil feature.";
                    tx.Abort();
                    return false;
                }

                CreateExternalClearanceEnvelopeCut(
                    doc,
                    basePoint,
                    threadAxis,
                    radialAxis,
                    context.UsefulLengthCm,
                    radialClearance,
                    centerRadius,
                    topRadius,
                    context.IsInteriorFace);

                CreateExternalStartChamfer(
                    doc,
                    basePoint,
                    threadAxis,
                    radialAxis,
                    effectivePreset,
                    baseRadius,
                    topRadius,
                    context.IsInteriorFace);

                CreateLeadOutRamp(
                    doc,
                    basePoint,
                    threadAxis,
                    radialAxis,
                    effectivePreset,
                    baseRadius,
                    topRadius,
                    context.UsefulLengthCm,
                    leadOutLength,
                    clockwise,
                    context.IsInteriorFace);

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

        private static double GetExternalStartOffsetCm(PrintThreadPreset preset)
        {
            double offset = preset.BaseWidthCm;
            double minOffset = ThreadWorker.ThresholdPitchCm;
            offset = Math.Max(offset, minOffset);

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "External start offset: baseWidth={0}cm pitch={1}cm offset={2}cm",
                preset.BaseWidthCm,
                preset.PitchCm,
                offset));

            return offset;
        }

        private static void CreateExternalClearanceEnvelopeCut(
            PartDocument doc,
            Point basePoint,
            UnitVector threadAxis,
            UnitVector radialAxis,
            double usefulLength,
            double radialClearance,
            double centerRadius,
            double topRadius,
            bool isInteriorFace)
        {
            if (isInteriorFace || radialClearance <= 0.0)
            {
                return;
            }

            double cutOuterRadius = centerRadius + Math.Max(radialClearance * 0.25, 0.005);
            if (topRadius <= 0.0 || topRadius >= cutOuterRadius || usefulLength <= 0.0)
            {
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "External clearance envelope cut skipped: topRadius={0}cm outerRadius={1}cm usefulLength={2}cm",
                    topRadius,
                    cutOuterRadius,
                    usefulLength));
                return;
            }

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

                Point2d p1 = _Tg.CreatePoint2d(0.0, topRadius);
                Point2d p2 = _Tg.CreatePoint2d(0.0, cutOuterRadius);
                Point2d p3 = _Tg.CreatePoint2d(usefulLength, cutOuterRadius);
                Point2d p4 = _Tg.CreatePoint2d(usefulLength, topRadius);

                SketchPoint sp1 = sketch.SketchPoints.Add(p1, false);
                SketchPoint sp2 = sketch.SketchPoints.Add(p2, false);
                SketchPoint sp3 = sketch.SketchPoints.Add(p3, false);
                SketchPoint sp4 = sketch.SketchPoints.Add(p4, false);

                ObjectCollection segments = _Application.TransientObjects.CreateObjectCollection();
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp1, sp2));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp2, sp3));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp3, sp4));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp4, sp1));

                Profile profile = sketch.Profiles.AddForSolid(false, segments, null);
                RevolveFeature envelopeCut = doc.ComponentDefinition.Features.RevolveFeatures.AddFull(
                    profile,
                    sketchAxis,
                    PartFeatureOperationEnum.kCutOperation);

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "External clearance envelope cut: created={0} length={1}cm topRadius={2}cm outerRadius={3}cm health={4}",
                    envelopeCut != null,
                    usefulLength,
                    topRadius,
                    cutOuterRadius,
                    envelopeCut == null ? "<null>" : envelopeCut.HealthStatus.ToString()));
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("External clearance envelope cut failed.", ex);
            }
        }

        private static void CreateExternalStartChamfer(
            PartDocument doc,
            Point basePoint,
            UnitVector threadAxis,
            UnitVector radialAxis,
            PrintThreadPreset preset,
            double baseRadius,
            double topRadius,
            bool isInteriorFace)
        {
            if (isInteriorFace)
            {
                return;
            }

            double chamferLength = Math.Min(preset.BaseWidthCm * 0.5, preset.PitchCm * 0.25);
            double reducedRadius = baseRadius;
            double cutOuterRadius = topRadius + Math.Max(preset.HeightCm * 0.10, 0.01);
            if (chamferLength <= 0.0 || reducedRadius >= cutOuterRadius)
            {
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "External start chamfer skipped: length={0}cm reducedRadius={1}cm outerRadius={2}cm",
                    chamferLength,
                    reducedRadius,
                    cutOuterRadius));
                return;
            }

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

                Point2d p1 = _Tg.CreatePoint2d(0.0, reducedRadius);
                Point2d p2 = _Tg.CreatePoint2d(0.0, cutOuterRadius);
                Point2d p3 = _Tg.CreatePoint2d(chamferLength, cutOuterRadius);
                Point2d p4 = _Tg.CreatePoint2d(chamferLength, topRadius);

                SketchPoint sp1 = sketch.SketchPoints.Add(p1, false);
                SketchPoint sp2 = sketch.SketchPoints.Add(p2, false);
                SketchPoint sp3 = sketch.SketchPoints.Add(p3, false);
                SketchPoint sp4 = sketch.SketchPoints.Add(p4, false);

                ObjectCollection segments = _Application.TransientObjects.CreateObjectCollection();
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp1, sp2));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp2, sp3));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp3, sp4));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp4, sp1));

                Profile profile = sketch.Profiles.AddForSolid(false, segments, null);
                RevolveFeature chamfer = doc.ComponentDefinition.Features.RevolveFeatures.AddFull(
                    profile,
                    sketchAxis,
                    PartFeatureOperationEnum.kCutOperation);

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "External start chamfer: created={0} length={1}cm reducedRadius={2}cm topRadius={3}cm cutOuterRadius={4}cm health={5}",
                    chamfer != null,
                    chamferLength,
                    reducedRadius,
                    topRadius,
                    cutOuterRadius,
                    chamfer == null ? "<null>" : chamfer.HealthStatus.ToString()));
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("External start chamfer failed.", ex);
            }
        }

        private static void BalanceRampLengths(
            PrintThreadContext context,
            PrintThreadPreset preset,
            ref double leadOutLength)
        {
            double minMainHeight = Math.Max(preset.PitchCm, context.PitchCm);
            double maxLeadOut = context.UsefulLengthCm - minMainHeight;
            if (maxLeadOut <= 0.0)
            {
                leadOutLength = 0.0;
                return;
            }

            if (leadOutLength <= maxLeadOut)
            {
                return;
            }

            leadOutLength = maxLeadOut;

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Ramp lengths balanced: leadOut={0}cm minMain={1}cm usefulLength={2}cm",
                leadOutLength,
                minMainHeight,
                context.UsefulLengthCm));
        }

        private static void CreateLeadOutRamp(
            PartDocument doc,
            Point basePoint,
            UnitVector threadAxis,
            UnitVector radialAxis,
            PrintThreadPreset preset,
            double baseRadius,
            double topRadius,
            double usefulLength,
            double leadOutLength,
            bool clockwise,
            bool isInteriorFace)
        {
            if (isInteriorFace)
            {
                DebugLog.WriteLine("Interior lead-out ramp skipped: additive interior thread uses the full clean profile.");
                return;
            }

            if (usefulLength <= 0.0 || leadOutLength <= 0.0)
            {
                DebugLog.WriteLine("Lead-out ramp skipped: invalid length.");
                return;
            }

            double rampStartDistance = Math.Max(0.0, usefulLength - leadOutLength);
            double actualLength = usefulLength - rampStartDistance;
            if (actualLength <= 0.0)
            {
                DebugLog.WriteLine("Lead-out ramp skipped: invalid bounded length.");
                return;
            }

            double rampAngle = Math.Atan(preset.HeightCm / actualLength);
            rampAngle = Math.Max(0.02, rampAngle);
            double signedTaper = isInteriorFace ? -rampAngle : rampAngle;

            double radialShift = Math.Tan(rampAngle) * actualLength;
            double endBaseRadius = isInteriorFace
                ? baseRadius - radialShift
                : baseRadius + radialShift;
            double endTopRadius = isInteriorFace
                ? topRadius - radialShift
                : topRadius + radialShift;

            if (endBaseRadius <= 0.0 || endTopRadius <= 0.0)
            {
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Lead-out ramp skipped: invalid end radii base={0}cm top={1}cm",
                    endBaseRadius,
                    endTopRadius));
                return;
            }

            Point rampBasePoint = OffsetPoint(basePoint, threadAxis, rampStartDistance);
            UnitVector rampRadialAxis = GetSectionRadialAxis(
                radialAxis,
                threadAxis,
                rampStartDistance,
                preset.PitchCm,
                clockwise);
            CoilFeature rampCoil;
            bool created = TryCreateCoilSection(
                doc,
                rampBasePoint,
                threadAxis,
                rampRadialAxis,
                preset,
                baseRadius,
                topRadius,
                isInteriorFace,
                actualLength,
                signedTaper,
                clockwise,
                out rampCoil);

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Lead-out ramp bounded: created={0} startDistance={1}cm length={2}cm taper={3}rad startBaseRadius={4}cm startTopRadius={5}cm endBaseRadius={6}cm endTopRadius={7}cm usefulLength={8}cm interior={9}",
                created,
                rampStartDistance,
                actualLength,
                signedTaper,
                baseRadius,
                topRadius,
                endBaseRadius,
                endTopRadius,
                usefulLength,
                isInteriorFace));
        }

        private static void CreateInteriorLeadOutChamfer(
            PartDocument doc,
            Point basePoint,
            UnitVector threadAxis,
            UnitVector radialAxis,
            PrintThreadPreset preset,
            double baseRadius,
            double topRadius,
            double usefulLength,
            double leadOutLength)
        {
            if (usefulLength <= 0.0 || leadOutLength <= 0.0)
            {
                DebugLog.WriteLine("Interior lead-out chamfer skipped: invalid length.");
                return;
            }

            if (topRadius <= 0.0 || baseRadius <= topRadius)
            {
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Interior lead-out chamfer skipped: invalid radii base={0}cm top={1}cm",
                    baseRadius,
                    topRadius));
                return;
            }

            double startDistance = Math.Max(0.0, usefulLength - leadOutLength);
            double actualLength = usefulLength - startDistance;
            if (actualLength <= 0.0)
            {
                DebugLog.WriteLine("Interior lead-out chamfer skipped: invalid bounded length.");
                return;
            }

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

                double innerRadius = topRadius - Math.Max(preset.HeightCm * 0.05, 0.002);
                if (innerRadius <= 0.0)
                {
                    DebugLog.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Interior lead-out chamfer skipped: invalid inner radius inner={0}cm",
                        innerRadius));
                    return;
                }

                Point2d p1 = _Tg.CreatePoint2d(startDistance, baseRadius);
                Point2d p2 = _Tg.CreatePoint2d(usefulLength, innerRadius);
                Point2d p3 = _Tg.CreatePoint2d(usefulLength, baseRadius);

                SketchPoint sp1 = sketch.SketchPoints.Add(p1, false);
                SketchPoint sp2 = sketch.SketchPoints.Add(p2, false);
                SketchPoint sp3 = sketch.SketchPoints.Add(p3, false);

                ObjectCollection segments = _Application.TransientObjects.CreateObjectCollection();
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp1, sp2));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp2, sp3));
                segments.Add(sketch.SketchLines.AddByTwoPoints(sp3, sp1));

                Profile profile = sketch.Profiles.AddForSolid(false, segments, null);
                RevolveFeature chamfer = doc.ComponentDefinition.Features.RevolveFeatures.AddFull(
                    profile,
                    sketchAxis,
                    PartFeatureOperationEnum.kCutOperation);

                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Interior lead-out chamfer: created={0} startDistance={1}cm length={2}cm baseRadius={3}cm innerRadius={4}cm usefulLength={5}cm health={6}",
                    chamfer != null,
                    startDistance,
                    actualLength,
                    baseRadius,
                    innerRadius,
                    usefulLength,
                    chamfer == null ? "<null>" : chamfer.HealthStatus.ToString()));
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("Interior lead-out chamfer failed.", ex);
            }
        }

        private static double GetLeadOutLengthCm(PrintThreadContext context, PrintThreadPreset preset)
        {
            double minLength = Math.Max(preset.PitchCm, context.PitchCm);
            double maxLength = context.UsefulLengthCm - minLength;
            if (maxLength <= 0.0)
            {
                return 0.0;
            }

            maxLength = Math.Min(maxLength, Math.Max(minLength, context.UsefulLengthCm * 0.25));
            double targetLength = Math.Max(minLength, preset.HeightCm * 2.0);
            return GetPhaseSafeRampLengthCm(targetLength, minLength, maxLength, preset.PitchCm);
        }

        private static double GetPhaseSafeRampLengthCm(
            double targetLength,
            double minLength,
            double maxLength,
            double pitch)
        {
            if (pitch <= 0.0 || maxLength <= 0.0)
            {
                return Math.Min(targetLength, maxLength);
            }

            double minTurns = Math.Max(1.0, Math.Ceiling(minLength / pitch));
            double targetTurns = Math.Max(minTurns, Math.Round(targetLength / pitch));
            double maxTurns = Math.Floor(maxLength / pitch);

            if (maxTurns >= minTurns)
            {
                double turns = Math.Min(Math.Max(targetTurns, minTurns), maxTurns);
                double length = turns * pitch;
                DebugLog.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Ramp length phase-safe: target={0}cm min={1}cm max={2}cm pitch={3}cm turns={4} length={5}cm",
                    targetLength,
                    minLength,
                    maxLength,
                    pitch,
                    turns,
                    length));
                return length;
            }

            double fallback = Math.Min(targetLength, maxLength);
            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Ramp length phase fallback: target={0}cm min={1}cm max={2}cm pitch={3}cm length={4}cm",
                targetLength,
                minLength,
                maxLength,
                pitch,
                fallback));
            return fallback;
        }

        private static double GetInteriorProfileAxialWidthCm(PrintThreadPreset preset)
        {
            double minWidth = Math.Min(Math.Max(preset.PitchCm * 0.02, 0.002), preset.PitchCm * 0.20);
            double baseWidth = Math.Max(minWidth, preset.PitchCm - preset.TopWidthCm);
            double topWidth = Math.Max(minWidth, preset.PitchCm - preset.BaseWidthCm);
            return Math.Max(baseWidth, topWidth);
        }

        private static double GetRadialClearanceCm(PrintThreadPreset preset)
        {
            return preset.ClearanceCm * 0.5;
        }

        private static PrintThreadPreset BuildEffectivePreset(PrintThreadPreset preset)
        {
            double radialClearance = GetRadialClearanceCm(preset);
            double minHeight = ThreadWorker.ThresholdPitchCm;

            return new PrintThreadPreset
            {
                Name = preset.Name,
                BaseWidthCm = preset.BaseWidthCm - preset.ClearanceCm,
                TopWidthCm = preset.TopWidthCm - preset.ClearanceCm,
                HeightCm = Math.Max(preset.HeightCm - radialClearance, minHeight),
                PitchCm = preset.PitchCm,
                ClearanceCm = preset.ClearanceCm
            };
        }

        private static UnitVector GetSectionRadialAxis(
            UnitVector radialAxis,
            UnitVector threadAxis,
            double distance,
            double pitch,
            bool clockwise)
        {
            if (radialAxis == null || threadAxis == null || pitch <= 0.0)
            {
                return radialAxis;
            }

            double angle = (2.0 * Math.PI * distance) / pitch;
            if (clockwise)
            {
                angle = -angle;
            }

            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            double vx = radialAxis.X;
            double vy = radialAxis.Y;
            double vz = radialAxis.Z;
            double ax = threadAxis.X;
            double ay = threadAxis.Y;
            double az = threadAxis.Z;

            double dot = (ax * vx) + (ay * vy) + (az * vz);
            double crossX = (ay * vz) - (az * vy);
            double crossY = (az * vx) - (ax * vz);
            double crossZ = (ax * vy) - (ay * vx);

            UnitVector result = _Tg.CreateUnitVector(
                (vx * cos) + (crossX * sin) + (ax * dot * (1.0 - cos)),
                (vy * cos) + (crossY * sin) + (ay * dot * (1.0 - cos)),
                (vz * cos) + (crossZ * sin) + (az * dot * (1.0 - cos)));

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Section radial axis: distance={0}cm pitch={1}cm clockwise={2} angle={3}rad axis=({4},{5},{6})",
                distance,
                pitch,
                clockwise,
                angle,
                result.X,
                result.Y,
                result.Z));

            return result;
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
            double minCutWidth = Math.Min(Math.Max(preset.PitchCm * 0.02, 0.002), preset.PitchCm * 0.20);
            double cutBaseWidth = isInteriorFace
                ? Math.Max(minCutWidth, preset.PitchCm - preset.TopWidthCm)
                : Math.Max(minCutWidth, preset.PitchCm - preset.BaseWidthCm);
            double cutTopWidth = isInteriorFace
                ? Math.Max(minCutWidth, preset.PitchCm - preset.BaseWidthCm)
                : Math.Max(minCutWidth, preset.PitchCm - preset.TopWidthCm);
            double height = preset.HeightCm;

            double maxWidth = Math.Max(cutBaseWidth, cutTopWidth);
            double baseInset = (maxWidth - cutBaseWidth) * 0.5;
            double topInset = (maxWidth - cutTopWidth) * 0.5;

            DebugLog.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "Trapezoid profile: visibleBaseWidth={0}cm visibleTopWidth={1}cm pitch={2}cm cutBaseWidth={3}cm cutTopWidth={4}cm height={5}cm baseRadius={6}cm topRadius={7}cm interior={8}",
                preset.BaseWidthCm,
                preset.TopWidthCm,
                preset.PitchCm,
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

            BuildSharpTrapezoidProfile(sketch, p1, p2, p3, p4, segments);

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

        private static bool TryCreateCoilSection(
            PartDocument doc,
            Point basePoint,
            UnitVector threadAxis,
            UnitVector radialAxis,
            PrintThreadPreset preset,
            double baseRadius,
            double topRadius,
            bool isInteriorFace,
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
                    "TryCreateCoilSection: basePoint=({0},{1},{2}) coilHeight={3} taper={4} clockwise={5} baseRadius={6} topRadius={7} interior={8} profileSegments={9}",
                    basePoint.X,
                    basePoint.Y,
                    basePoint.Z,
                    coilHeight,
                    taper,
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
                    isInteriorFace
                        ? PartFeatureOperationEnum.kJoinOperation
                        : PartFeatureOperationEnum.kCutOperation,
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

            if (preset.ClearanceCm < 0.0)
            {
                errorMessage = "Clearance must be greater than or equal to zero.";
                return false;
            }

            if (preset.TopWidthCm >= preset.BaseWidthCm)
            {
                errorMessage = "Top width must be smaller than base width.";
                return false;
            }

            if (preset.ClearanceCm >= preset.TopWidthCm * 0.8)
            {
                errorMessage = "Clearance is too large for the requested top width.";
                return false;
            }

            double radialClearance = GetRadialClearanceCm(preset);
            if (preset.HeightCm - radialClearance <= ThreadWorker.ThresholdPitchCm)
            {
                errorMessage = "Clearance leaves too little thread height.";
                return false;
            }

            PrintThreadPreset effectivePreset = BuildEffectivePreset(preset);

            if (preset.PitchCm < ThreadWorker.ThresholdPitchCm)
            {
                errorMessage = "Pitch is too small.";
                return false;
            }

            double nominalRadius = context.NominalDiameterCm * 0.5;
            if (nominalRadius <= effectivePreset.BaseWidthCm * 0.5)
            {
                errorMessage = "Nominal diameter is too small for the requested profile.";
                return false;
            }

            if (effectivePreset.PitchCm < effectivePreset.BaseWidthCm * 0.75)
            {
                errorMessage = "Pitch is too small compared to the profile width.";
                return false;
            }

            if (context != null && context.IsInteriorFace)
            {
                double interiorMainHeight = context.UsefulLengthCm - GetInteriorProfileAxialWidthCm(effectivePreset);
                if (interiorMainHeight < effectivePreset.PitchCm)
                {
                    errorMessage = "Selected internal thread is too short for this profile.";
                    return false;
                }
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
