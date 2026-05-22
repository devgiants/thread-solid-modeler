////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) Autodesk, Inc. All rights reserved 
// Written by Philippe Leefsma 2011 - ADN/Developer Technical Services
//
// This software is provided as is, without any warranty that it will work. You choose to use this tool at your own risk.
// Neither Autodesk nor the author Philippe Leefsma can be taken as responsible for any damage this tool can cause to 
// your data. Please always make a back up of your data prior to use this tool, as it will modify the documents involved 
// in the feature transformation.
//
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using Inventor;
using ThreadModeler.Utilities;

namespace ThreadModeler
{
    /////////////////////////////////////////////////////////////////
    // Provides Thread-specific utilities
    //
    /////////////////////////////////////////////////////////////////
    class ThreadWorker
    {
        private static TransientGeometry _Tg;

        private static Inventor.Application _Application;

        private static bool _ConstructionWorkFeature = true;

        public const double ThresholdPitchCm = 0.001778;  

        /////////////////////////////////////////////////////////////
        //use: Initialize the Toolkit library
        //
        /////////////////////////////////////////////////////////////
        public static void Initialize(
            Inventor.Application Application)
        {
            _Application = Application;

            _Tg = _Application.TransientGeometry;
        }

        /////////////////////////////////////////////////////////////
        // Use: High-level method that modelizes a collection of
        //      ThreadFeatures.
        /////////////////////////////////////////////////////////////
        /////////////////////////////////////////////////////////////
        // Use: High-level method that modelizes a collection of
        //      ThreadFeatures.
        /////////////////////////////////////////////////////////////
        public static bool ModelizeThreads(PartDocument doc,
            PlanarSketch templateSketch,
            IEnumerable<ThreadFeature> threads,
            double extraPitch)
        {
            bool ret = true;
            DebugLog.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "ModelizeThreads start: extraPitch={0}",
                extraPitch));

            foreach (ThreadFeature thread in threads)
            {
                DebugLog.WriteLine("ModelizeThreads thread=" + thread.Name + " type=" + thread.ThreadInfoType);
                switch (thread.ThreadInfoType)
                { 
                    case ThreadTypeEnum.kStandardThread:

                        if (!ThreadWorker.ModelizeThreadStandard(doc,
                            thread as PartFeature,
                            templateSketch,
                            thread.ThreadInfo,
                            thread.ThreadedFace[1],
                            extraPitch))

                            ret = false;

                        break;

                    case ThreadTypeEnum.kTaperedThread:

                        if (!ThreadWorker.ModelizeThreadTapered(doc, 
                            thread as PartFeature,
                            templateSketch,
                            thread.ThreadInfo,
                            thread.ThreadedFace[1],
                            extraPitch))

                            ret = false;

                        break;

                    default:
                        break;
                }
            }

            return ret;
        }

        /////////////////////////////////////////////////////////////
        // Use: Modelizes a Standard ThreadFeature.
        //
        /////////////////////////////////////////////////////////////
        public static bool ModelizeThreadStandard(PartDocument doc, 
            PartFeature feature,
            PlanarSketch templateSketch, 
            ThreadInfo threadInfo,
            Face threadedFace,
            double extraPitch)
        {
            Transaction Tx = 
             _Application.TransactionManager.StartTransaction(
                doc as _Document,
                "Modelizing Thread " + feature.Name);

            try
            {
                DebugLog.WriteLine("ModelizeThreadStandard start for " + feature.Name);
                LogThreadContext("standard", threadInfo, threadedFace, extraPitch);

                double pitch = 
                    ThreadWorker.GetThreadPitch(threadInfo);

                Vector threadDirection = 
                    threadInfo.ThreadDirection;

                bool isInteriorFace = 
                    Toolkit.IsInteriorFace(threadedFace);

                Point basePoint = 
                    threadInfo.ThreadBasePoints[1] as Point;

                if (isInteriorFace)
                {
                    Vector normal = 
                        Toolkit.GetOrthoVector(
                        threadDirection.AsUnitVector()).AsVector();

                    normal.ScaleBy(
                        ThreadWorker.GetThreadMajorRadiusStandard(
                            threadedFace));

                    basePoint.TranslateBy(normal);
                }

                PlanarSketch newSketch = Toolkit.InsertSketch(doc,
                    templateSketch,
                    threadDirection.AsUnitVector(),
                    Toolkit.GetOrthoVector(
                        threadDirection.AsUnitVector()),
                    basePoint);

                Point coilBase = 
                    threadInfo.ThreadBasePoints[1] as Point;

                bool rightHanded = threadInfo.RightHanded;

                double taper = 0;

                SurfaceBody affectedBody;

                if (!ThreadWorker.InitializeForCoilStandard(doc, 
                    threadInfo,
                    threadedFace,
                    newSketch.Name, 
                    isInteriorFace, pitch,
                    out affectedBody))
                {
                    DebugLog.WriteLine("InitializeForCoilStandard returned false.");
                    Tx.Abort();
                    return false;
                }

                Profile profile =
                  newSketch.Profiles.AddForSolid(true, null, null);

                if (!ThreadWorker.CreateCoilFeature(doc,
                    profile,
                    threadDirection,
                    coilBase,
                    rightHanded,
                    taper,
                    pitch,
                    extraPitch,
                    affectedBody,
                    isInteriorFace))
                {
                    DebugLog.WriteLine("CreateCoilFeature returned false for standard thread " + feature.Name);
                    Tx.Abort();
                    return false;
                }

                newSketch.Shared = false;

                feature.Suppressed = true;

                Tx.End();

                return true;
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("ModelizeThreadStandard failed for " + feature.Name, ex);
                Tx.Abort();
                return false;
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Modelizes a Tapered ThreadFeature.
        //
        /////////////////////////////////////////////////////////////
        public static bool ModelizeThreadTapered(PartDocument doc,
            PartFeature feature,
            PlanarSketch templateSketch,
            ThreadInfo threadInfo,
            Face threadedFace,
            double extraPitch)
        {
            Transaction Tx = 
             _Application.TransactionManager.StartGlobalTransaction(
              doc as _Document,
              "Modelizing Thread ");

            try
            {
                DebugLog.WriteLine("ModelizeThreadTapered start for " + feature.Name);
                LogThreadContext("tapered", threadInfo, threadedFace, extraPitch);

                double pitch = 
                    ThreadWorker.GetThreadPitch(threadInfo);

                Vector threadDirection = 
                    threadInfo.ThreadDirection;

                Point coilBase = 
                    threadInfo.ThreadBasePoints[1] as Point;

                bool isInteriorFace = 
                    Toolkit.IsInteriorFace(threadedFace);

                Line sideDir = 
                    ThreadWorker.GetThreadSideDirection(
                        threadInfo,
                        threadedFace);

                Vector sketchYAxis = 
                    sideDir.RootPoint.VectorTo(coilBase);

                sketchYAxis.ScaleBy((isInteriorFace ? -1.0 : 1.0));

                Point sketchBasePoint = sideDir.RootPoint;

                sketchYAxis = sketchBasePoint.VectorTo(coilBase);

                PlanarSketch newSketch = Toolkit.InsertSketch(doc,
                    templateSketch,
                    sideDir.Direction,
                    sketchYAxis.AsUnitVector(),
                    sketchBasePoint);

                bool rightHanded = threadInfo.RightHanded;

                bool IsExpanding = ThreadWorker.IsExpanding(
                    threadInfo,
                    threadedFace);

                double taper = 
                    Math.Abs(threadDirection.AngleTo(
                        sideDir.Direction.AsVector())) 
                        * (IsExpanding ? 1.0 : -1.0);

                if (!ThreadWorker.InitializeForCoilTapered(doc, 
                    threadInfo,
                    threadedFace,
                    newSketch.Name, 
                    isInteriorFace, pitch))
                {
                    DebugLog.WriteLine("InitializeForCoilTapered returned false.");
                    Tx.Abort();
                    return false;
                }

                Profile profile = 
                    newSketch.Profiles.AddForSolid(true, 
                        null, 
                        null);

                SurfaceBody affectedBody;
                double depth = ThreadWorker.GetThreadMajorRadiusTapered(
                    threadInfo,
                    threadedFace);

                if (!ThreadWorker.CreateCoilBodyTapered(doc,
                    threadInfo,
                    threadedFace,
                    depth,
                    isInteriorFace,
                    out affectedBody))
                {
                    Tx.Abort();
                    return false;
                }

                if (!ThreadWorker.CreateCoilFeature(doc,
                    profile,
                    threadDirection,
                    coilBase,
                    rightHanded,
                    taper,
                    pitch,
                    extraPitch,
                    affectedBody,
                    isInteriorFace))
                {
                    DebugLog.WriteLine("CreateCoilFeature returned false for tapered thread " + feature.Name);
                    Tx.Abort();
                    return false;
                }

                newSketch.Shared = false;

                feature.Suppressed = true;

                Tx.End();

                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    DebugLog.WriteException("ModelizeThreadTapered failed for " + feature.Name, ex);
                    Tx.Abort();
                    return false;
                }
                catch
                {
                    return false;
                }
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Initializes parameters to create new solid bodies
        //      affected by the future CoilFeature for Standard
        //      Thread.
        /////////////////////////////////////////////////////////////
        private static bool InitializeForCoilStandard(
            PartDocument doc, 
            ThreadInfo threadInfo, 
            Face threadedFace,
            string sketchName,
            bool isInteriorFace,
            double pitchValue,
            out SurfaceBody affectedBody)
        {
            affectedBody = null;
            DebugLog.WriteLine("InitializeForCoilStandard sketch=" + sketchName);

            Parameter pitch =
                Toolkit.FindAndUpdateParameter(doc,
                    "Pitch",
                    sketchName);

            if (pitch == null)
                return false;

            Parameter offset = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "ThreadOffset", 
                    sketchName);

            if (offset == null)
                return false;

            Parameter major = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "MajorRadius", 
                    sketchName);

            if (major == null)
                return false;

            Parameter minor = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "MinorRadius", 
                    sketchName);

            if (minor == null)
                return false;

            DebugLog.WriteParameter("  pitch(before)", pitch);
            DebugLog.WriteParameter("  offset(before)", offset);
            DebugLog.WriteParameter("  major(before)", major);
            DebugLog.WriteParameter("  minor(before)", minor);

            pitch.Value = pitchValue;

            offset.Value = 0;

            double majorRad =
                ThreadWorker.GetThreadMajorRadiusStandard(
                    threadedFace);

            double minorValue = Math.Abs((double)minor.Value);

            major.Value = (isInteriorFace ? 0 : majorRad);
            DebugLog.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "  standard radii majorRad={0} minorValue={1} interior={2}",
                majorRad,
                minorValue,
                isInteriorFace));
 
            doc.Update();

            if (isInteriorFace)
            {
                major.Value = minorValue;
                doc.Update();
            }

                bool ret = ThreadWorker.CreateCoilBodyStandard(doc, 
                    threadInfo, 
                    threadedFace,
                    minorValue, 
                    majorRad,
                    isInteriorFace,
                    out affectedBody);

            return ret;
        }

        /////////////////////////////////////////////////////////////
        // Use: Initializes parameters to create new solid bodies
        //      affected by the future CoilFeature for Tapered
        //      Thread.
        /////////////////////////////////////////////////////////////
        private static bool InitializeForCoilTapered(
            PartDocument doc,
            ThreadInfo threadInfo,
            Face threadedFace,
            string sketchName,
            bool isInteriorFace,
            double pitchValue)
        {
            DebugLog.WriteLine("InitializeForCoilTapered sketch=" + sketchName);

            Parameter pitch = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "Pitch", 
                    sketchName);

            if (pitch == null)
                return false;

            Parameter offset = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "ThreadOffset", 
                    sketchName);

            if (offset == null)
                return false;

            Parameter major = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "MajorRadius", 
                    sketchName);

            if (major == null)
                return false;

            Parameter minor = 
                Toolkit.FindAndUpdateParameter(doc, 
                    "MinorRadius", 
                    sketchName);

            if (minor == null)
                return false;

            DebugLog.WriteParameter("  pitch(before)", pitch);
            DebugLog.WriteParameter("  offset(before)", offset);
            DebugLog.WriteParameter("  major(before)", major);
            DebugLog.WriteParameter("  minor(before)", minor);

            pitch.Value = pitchValue;

            offset.Value = 0;

            double minorValue = Math.Abs((double)minor.Value);

            double majorRad = 0;

            major.Value = (isInteriorFace ? 0 : majorRad);
            DebugLog.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "  tapered values minorValue={0} interior={1}",
                minorValue,
                isInteriorFace));

            doc.Update();

            return true;
        }

        /////////////////////////////////////////////////////////////
        // Use: Creates new solid bodies affected by the future
        //      CoilFeature for Standard Thread.
        /////////////////////////////////////////////////////////////
        private static bool CreateCoilBodyStandard(PartDocument doc,
            ThreadInfo threadInfo,
            Face threadedFace,
            double minorRad,
            double majorRad,
            bool isInteriorFace,
            out SurfaceBody affectedBody)
        {
            try
            {
                affectedBody = null;
                DebugLog.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "CreateCoilBodyStandard minorRad={0} majorRad={1} interior={2}",
                    minorRad,
                    majorRad,
                    isInteriorFace));

                if (isInteriorFace)
                {
                    affectedBody = threadedFace.SurfaceBody as SurfaceBody;
                    DebugLog.WriteLine("CreateCoilBodyStandard interior uses host body=" +
                        (affectedBody == null ? "<null>" : affectedBody.Name));
                    return (affectedBody != null);
                }

                affectedBody = threadedFace.SurfaceBody as SurfaceBody;
                DebugLog.WriteLine("CreateCoilBodyStandard exterior uses host body=" +
                    (affectedBody == null ? "<null>" : affectedBody.Name));
                return (affectedBody != null);
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("CreateCoilBodyStandard failed.", ex);
                affectedBody = null;
                return false;
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Creates new solid bodies affected by the future
        //      CoilFeature for Tapered Thread.
        /////////////////////////////////////////////////////////////
        private static bool CreateCoilBodyTapered(PartDocument doc,
            ThreadInfo threadInfo,
            Face threadedFace,
            double depth,
            bool isInteriorFace,
            out SurfaceBody affectedBody)
        {
            try
            {
                affectedBody = null;
                DebugLog.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "CreateCoilBodyTapered depth={0} interior={1}",
                    depth,
                    isInteriorFace));

                if (isInteriorFace)
                {
                    affectedBody = threadedFace.SurfaceBody as SurfaceBody;
                    DebugLog.WriteLine("CreateCoilBodyTapered interior uses host body=" +
                        (affectedBody == null ? "<null>" : affectedBody.Name));
                    return (affectedBody != null);
                }

                affectedBody = threadedFace.SurfaceBody as SurfaceBody;
                DebugLog.WriteLine("CreateCoilBodyTapered exterior uses host body=" +
                    (affectedBody == null ? "<null>" : affectedBody.Name));
                return (affectedBody != null);
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("CreateCoilBodyTapered failed.", ex);
                affectedBody = null;
                return false;
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Creates CoilFeature that represents the modelized
        //      Thread.
        /////////////////////////////////////////////////////////////
        private static bool CreateCoilFeature(PartDocument doc,
            Profile profile,
            Vector threadDirection,
            Point basePoint,
            bool rightHanded,
            double taper,
            double pitch,
            double extraPitch,
            SurfaceBody affectedBody,
            bool isInteriorFace)
        {
            try
            {
                DebugLog.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "CreateCoilFeature pitch={0} extraPitch={1} taper={2} rightHanded={3} affectedBody={4}",
                    pitch,
                    extraPitch,
                    taper,
                    rightHanded,
                    affectedBody == null ? "<null>" : affectedBody.Name));

                PartComponentDefinition compDef = 
                    doc.ComponentDefinition;

                WorkAxis wa = compDef.WorkAxes.AddFixed(
                    basePoint, 
                    threadDirection.AsUnitVector(), 
                    _ConstructionWorkFeature);
            
                double height = threadDirection.Length + 2 * pitch;
            
                double coilPitch = pitch * (100 + extraPitch) * 0.01;
            
                CoilFeature coil =
                  compDef.Features.CoilFeatures.AddByPitchAndHeight(
                        profile,
                        wa,
                        coilPitch,
                        height, 
                        PartFeatureOperationEnum.kCutOperation,
                        false,
                        !rightHanded,
                        taper, 
                        false, 
                        0, 
                        0, 
                        false,
                        0, 
                        0);
                 
                if (affectedBody != null && !isInteriorFace)
                {
                    DebugLog.WriteLine("CreateCoilFeature exterior uses default affected body resolution.");
                }

                DebugLog.WriteLine("CreateCoilFeature health=" + coil.HealthStatus);
                 
                return (coil.HealthStatus == HealthStatusEnum.kUpToDateHealth);
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("CreateCoilFeature failed.", ex);
                return false;
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns True if path argument contains same sketch
        //      entity than mainPath.
        /////////////////////////////////////////////////////////////
        private static bool HasMatchingEntity(ProfilePath path, 
            ProfilePath mainPath)
        {
            foreach (ProfileEntity profileEnt1 in path)
            {
                foreach (ProfileEntity profileEnt2 in mainPath)
                {
                    if (profileEnt1.SketchEntity == 
                        profileEnt2.SketchEntity)
                    {
                        if (profileEnt1.SketchEntity is SketchLine)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns Thread Pitch value as double (in cm).
        //
        /////////////////////////////////////////////////////////////
        private static void LogThreadContext(string mode,
            ThreadInfo threadInfo,
            Face threadedFace,
            double extraPitch)
        {
            try
            {
                DebugLog.WriteLine("Context mode=" + mode);
                DebugLog.WriteValue("  threadInfoType", threadInfo == null ? "<null>" : threadInfo.GetType().Name);
                DebugLog.WriteValue("  faceType", threadedFace == null ? "<null>" : threadedFace.SurfaceType.ToString());
                DebugLog.WriteValue("  extraPitch", extraPitch);
                DebugLog.WriteValue("  rightHanded", threadInfo == null ? "<null>" : threadInfo.RightHanded.ToString());
                DebugLog.WriteInventorPoint("  basePoint", threadInfo == null ? null : threadInfo.ThreadBasePoints[1] as Point);
                DebugLog.WriteInventorVector("  threadDirection", threadInfo == null ? null : threadInfo.ThreadDirection);
                if (threadInfo != null)
                {
                    DebugLog.WriteValue("  pitch(cm)", ThreadWorker.GetThreadPitch(threadInfo).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    LogThreadMetadata(threadInfo);
                }
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("LogThreadContext failed.", ex);
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Logs the thread metadata exposed by Inventor so we
        //      can validate the generator against the catalog data.
        //
        /////////////////////////////////////////////////////////////
        private static void LogThreadMetadata(ThreadInfo threadInfo)
        {
            try
            {
                StandardThreadInfo standardThread = threadInfo as StandardThreadInfo;
                if (standardThread != null)
                {
                    DebugLog.WriteValue("  threadDesignation", standardThread.ThreadDesignation);
                    DebugLog.WriteValue("  nominalSize", standardThread.NominalSize);
                    DebugLog.WriteValue("  threadClass", standardThread.Class);
                    DebugLog.WriteValue("  internal", standardThread.Internal);
                    DebugLog.WriteValue("  fullThreadDepth", standardThread.FullThreadDepth);
                    DebugLog.WriteValue("  majorDiameterMax", standardThread.MajorDiameterMax);
                    DebugLog.WriteValue("  majorDiameterMin", standardThread.MajorDiameterMin);
                    DebugLog.WriteValue("  minorDiameterMax", standardThread.MinorDiameterMax);
                    DebugLog.WriteValue("  minorDiameterMin", standardThread.MinorDiameterMin);
                    DebugLog.WriteValue("  pitchDiameterMax", standardThread.PitchDiameterMax);
                    DebugLog.WriteValue("  pitchDiameterMin", standardThread.PitchDiameterMin);
                    DebugLog.WriteValue("  tapDrillDiameter", standardThread.TapDrillDiameter);
                }
                else
                {
                    DebugLog.WriteValue("  threadDesignation", threadInfo == null ? "<null>" : threadInfo.ThreadDesignation);
                    DebugLog.WriteValue("  internal", threadInfo == null ? "<null>" : threadInfo.Internal.ToString());
                    DebugLog.WriteValue("  fullThreadDepth", threadInfo == null ? "<null>" : threadInfo.FullThreadDepth.ToString());
                }
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("LogThreadMetadata failed.", ex);
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns Thread Pitch value as double (in cm).
        //
        /////////////////////////////////////////////////////////////
        public static double GetThreadPitch(ThreadInfo threadInfo)
        {
            bool metric = (bool)Toolkit.GetProperty(
                threadInfo,
                "Metric");

            double pitch = (double)Toolkit.GetProperty(
                threadInfo,
                "Pitch");

            return (metric ? pitch * 0.1 : pitch * 2.54);
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns Thread Pitch as string using default units.
        //
        /////////////////////////////////////////////////////////////
        public static string GetThreadPitchStr(ThreadInfo threadInfo, 
            Document doc)
        {
            double pitch = ThreadWorker.GetThreadPitch(threadInfo);

            UnitsOfMeasure uom = doc.UnitsOfMeasure;

            return uom.GetStringFromValue(pitch, 
                UnitsTypeEnum.kDefaultDisplayLengthUnits);
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns thread major radius for standard thread.
        //
        /////////////////////////////////////////////////////////////
        private static double GetThreadMajorRadiusStandard(
            Face threadedFace)
        {
            System.Object radius = Toolkit.GetProperty(
               threadedFace.Geometry, 
               "Radius");

            return (double)radius;
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns thread major radius for tapered thread.
        //
        /////////////////////////////////////////////////////////////
        private static double GetThreadMajorRadiusTapered(
            ThreadInfo threadInfo,
            Face threadedFace)
        {
            UnitVector yAxis =
                threadInfo.ThreadDirection.AsUnitVector();

            UnitVector xAxis = Toolkit.GetOrthoVector(yAxis);

            Point basePoint =
                threadInfo.ThreadBasePoints[1] as Point;

            Line l1 = Toolkit.GetFaceSideDirection(
                threadedFace.Geometry, xAxis);

            Line l2 = _Tg.CreateLine(basePoint, xAxis.AsVector());

            Point p1 = l1.IntersectWithCurve(l2, 0.0001)[1] as Point;

            return p1.DistanceTo(basePoint);
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns thread type as string.
        //
        /////////////////////////////////////////////////////////////
        public static string GetThreadTypeStr(PartFeature feature)
        {
            if (feature.Type == ObjectTypeEnum.kHoleFeatureObject)
                return "Standard";

            if (feature.Type == ObjectTypeEnum.kThreadFeatureObject || feature.Type == ObjectTypeEnum.kThreadFeatureProxyObject)
            {
                ThreadFeature thread = feature as ThreadFeature;

                return (thread.ThreadInfoType ==
                    ThreadTypeEnum.kStandardThread ?
                        "Standard" : "Tapered");
            }

            return "Invalid Feature";
        }

        /////////////////////////////////////////////////////////////
        // Use: Return threaded face type as string.
        //
        /////////////////////////////////////////////////////////////
        public static string GetThreadedFaceTypeStr(
            Face threadedFace)
        {
            try
            {
                return (Toolkit.IsInteriorFace(threadedFace) ? 
                    "Interior" : "Exterior");
            }
            catch
            {
                return "Unknown";
            }
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns direction of the thread side as Line object.
        //
        /////////////////////////////////////////////////////////////
        public static Line GetThreadSideDirection(ThreadInfo threadInfo, Face threadedFace)
        {
            Point RootPoint1 = threadInfo.ThreadBasePoints[1] as Point;
            Vector threadDirection = threadInfo.ThreadDirection;
            Point point = ThreadWorker._Tg.CreatePoint(RootPoint1.X + threadDirection.X, RootPoint1.Y + threadDirection.Y, RootPoint1.Z + threadDirection.Z);
            UnitVector orthoVector = Toolkit.GetOrthoVector(threadDirection.AsUnitVector());
            Line faceSideDirection = Toolkit.GetFaceSideDirection(threadedFace, orthoVector);
            Line line1 = ThreadWorker._Tg.CreateLine(RootPoint1, orthoVector.AsVector());
            Line line2 = ThreadWorker._Tg.CreateLine(point, orthoVector.AsVector());
            Point RootPoint2 = faceSideDirection.IntersectWithCurve((object)line1, 0.0001)[1] as Point;
            Point Point = faceSideDirection.IntersectWithCurve((object)line2, 0.0001)[1] as Point;
            return ThreadWorker._Tg.CreateLine(RootPoint2, RootPoint2.VectorTo(Point));
        }

        /////////////////////////////////////////////////////////////
        // Use: Returns True if conical thread is expanding.
        //      Works only for tapered threads.
        /////////////////////////////////////////////////////////////
        public static bool IsExpanding(
            ThreadInfo threadInfo,
            Face threadedFace)
        {
            Point basePoint = 
                threadInfo.ThreadBasePoints[1] as Point;

            Vector direction = threadInfo.ThreadDirection;

            Point endPoint = _Tg.CreatePoint(
                   basePoint.X + direction.X,
                   basePoint.Y + direction.Y,
                   basePoint.Z + direction.Z);

            UnitVector yAxis = direction.AsUnitVector();

            UnitVector xAxis = Toolkit.GetOrthoVector(yAxis);
            
            Line l1 = Toolkit.GetFaceSideDirection(threadedFace, xAxis);

            Line l2 = _Tg.CreateLine(basePoint, xAxis.AsVector());

            Line l3 = _Tg.CreateLine(endPoint, xAxis.AsVector());

            Point p1 = l1.IntersectWithCurve(l2, 0.0001)[1] as Point;
            Point p2 = l1.IntersectWithCurve(l3, 0.0001)[1] as Point;

            double dBase = p1.DistanceTo(basePoint);

            double dEnd = p2.DistanceTo(endPoint);

            return (dBase < dEnd);
        }
    }
}
