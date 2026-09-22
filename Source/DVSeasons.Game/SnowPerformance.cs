using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Local aggregate timings; no readback, network traffic, or per-frame log writes.
    internal static class SnowPerformance
    {
        private sealed class Counter { public long Ticks, Maximum; public int Count; }
        private static readonly Dictionary<string,Counter> counters=new Dictionary<string,Counter>();
        private static float nextReport;
        private static int frames;
        private static double frameSeconds;
        private static float maximumFrame;
        private static int slowFrames, lastCollections;
        private static long detailedDraws, exclusionDraws;
        private static long volumeCars, volumeCommands;
        private static long nativeMaterialSlots,combinedCommands,distantCars,distantCommands;
        private static long nativeShared,nativePrivate,nativeVariants;
        private static int nativeSamples;
        public static void NativeSharing(int shared,int perCar,int variants)
        {nativeSamples++;nativeShared+=shared;nativePrivate+=perCar;nativeVariants+=variants;}
        private static string NativeMean(long value)=>
            (nativeSamples>0?value/(double)nativeSamples:0).ToString("F1",CultureInfo.InvariantCulture);
        private static long instancedExclusions, exclusionBatches;
        private static long instancedFull, fullBatches;
        private static long schedulerComponents, schedulerPlans, schedulerHits, schedulerUploads, schedulerMatrices, schedulerPairs;
        private static long schedulerOverlaps, schedulerUnions;
        private static long schedulerOrientedTests, schedulerOrientedRejected;
        private static long readyStreamSamples, readyStreamTotal;
        private static int readyStreamMaximum, fullBatchMaximum;
        private static long activeGraphStreams, activeGraphEdges, activeGraphEmptyStreams, activeGraphEmptyEdges;
        private static int activeGraphSamples;
        private static int partGraphSubmissions,vehicleGraphSubmissions;
        public static void SurfaceGranularity(bool parts)
        {if(parts)partGraphSubmissions++;else vehicleGraphSubmissions++;}
        private static long cullingParts, cullingLodSkipped, cullingStateRejected, cullingFrustumTests, cullingLods;
        private static int cullingSamples, seasonBits;
        public static void VehicleCulling(int partsVisited,int partsLodSkipped,int partsStateRejected,int partsFrustumTested,int lodGroups)
        {
            cullingSamples++;cullingParts+=partsVisited;cullingLodSkipped+=partsLodSkipped;
            cullingStateRejected+=partsStateRejected;cullingFrustumTests+=partsFrustumTested;cullingLods+=lodGroups;
        }
        private static long topologyPoses, topologyEnvelopes, topologyPadding, topologyPublications;
        private static long batchingComponents, batchingVehicles, batchingMultiComponents;
        private static long batchingEligible, batchingUniqueMesh, batchingUniqueKey, batchingSkinned, batchingStatic, batchingMirrored, batchingStreams, batchingOther;
        private static int batchingSamples, batchingLargest;
        private static int renderedFrames, snowLimit, renderWidth, renderHeight;
        private static int sideSnowCars, sideSnowDepositing, sideSnowSheltered;
        private static float sideSnowMaximum, sideSnowfall;
        // Event totals for the current report window, independent of render
        // cameras. Call once per actual cache event, never with lifetime totals.
        public static void Topology(int pose,int envelope,int padding,int publications)
        {
            topologyPoses+=pose;topologyEnvelopes+=envelope;
            topologyPadding+=padding;topologyPublications+=publications;
        }
        // One snapshot per completed scheduler submission, including cache hits.
        // Component counts describe the selected scheduler's streams: whole
        // vehicles on the conservative path, visible parts on the fine path.
        // Eligibility counts are full surface draw requests only.
        // Uniqueness of a mesh in the registry is an ineligibility reason;
        // unique full draw keys in this camera are a subset of eligible draws.
        public static void VehicleBatching(int components,int largest,int componentVehicles,int multiVehicleComponents,
            int eligibleDraws,int uniqueMeshDraws,int skinnedDraws,int staticDraws,int mirroredDraws,int vertexStreamsDraws,int otherDraws,int uniqueKeyDraws)
        {
            batchingSamples++;batchingComponents+=components;batchingVehicles+=componentVehicles;
            batchingLargest=Math.Max(batchingLargest,largest);batchingMultiComponents+=multiVehicleComponents;
            batchingEligible+=eligibleDraws;batchingUniqueMesh+=uniqueMeshDraws;batchingUniqueKey+=uniqueKeyDraws;
            batchingSkinned+=skinnedDraws;batchingStatic+=staticDraws;batchingMirrored+=mirroredDraws;
            batchingStreams+=vertexStreamsDraws;batchingOther+=otherDraws;
        }
        // Frame snapshots, supplied exactly once after a completed scheduler
        // submission. Ready-stream counts describe planning decision points;
        // cached plans supply the same snapshots without redoing scheduling.
        public static void SchedulerBatching(long readySamples,long readyTotal,int readyMaximum,int fullMaximum)
        {
            readyStreamSamples+=readySamples;readyStreamTotal+=readyTotal;
            readyStreamMaximum=Math.Max(readyStreamMaximum,readyMaximum);
            fullBatchMaximum=Math.Max(fullBatchMaximum,fullMaximum);
        }
        // The geometric DAG still includes invisible streams. These snapshots
        // distinguish its current nonempty induced graph from direct edges
        // skipped because either endpoint has no draw requests this submission.
        public static void ActiveGraph(int activeStreams,int activeEdges,int emptyStreams,int emptyBridgeEdges)
        {
            activeGraphSamples++;activeGraphStreams+=activeStreams;activeGraphEdges+=activeEdges;
            activeGraphEmptyStreams+=emptyStreams;activeGraphEmptyEdges+=emptyBridgeEdges;
        }
        public static void SideSnow(int tracked,int depositing,int sheltered,float maximum,float snowfall)
        {
            sideSnowCars=tracked;sideSnowDepositing=depositing;sideSnowSheltered=sheltered;
            sideSnowMaximum=maximum;sideSnowfall=snowfall;
        }
        public static void SnowRender(int detailed,int excluded,int limit,int width,int height,
            int instanced=0,int batches=0,int fullInstanced=0,int fullBatchCount=0,int excludedVolumes=0,
            int nativeSlots=0,int combined=0,int distant=0,int distantDraws=0)
        {
            detailedDraws+=detailed;exclusionDraws+=excluded;renderedFrames++;
            instancedExclusions+=instanced;exclusionBatches+=batches;
            instancedFull+=fullInstanced;fullBatches+=fullBatchCount;
            volumeCars+=excludedVolumes;if(excludedVolumes>0)volumeCommands++;
            nativeMaterialSlots+=nativeSlots;combinedCommands+=combined;distantCars+=distant;distantCommands+=distantDraws;
            snowLimit=limit;renderWidth=width;renderHeight=height;
        }
        // Record deltas around each camera submission, rather than adding lifetime
        // counters every frame. This also handles multiple cameras and registry resets.
        public struct SchedulerScope : IDisposable
        {
            private readonly SnowVehicleDrawScheduler scheduler;
            private readonly int components, plans, hits, uploads, matrices;
            private readonly long pairs, overlaps, unions, orientedTests, orientedRejected;
            public SchedulerScope(SnowVehicleDrawScheduler value)
            {
                scheduler=value;components=value.ComponentBuilds;plans=value.PlanBuilds;
                hits=value.PlanCacheHits;uploads=value.FullMatrixUploads;
                matrices=value.FullMatrixElementsUploaded;pairs=value.BoundsPairTests;
                overlaps=value.ActualOverlaps;unions=value.ComponentUnions;
                orientedTests=value.OrientedPairTests;orientedRejected=value.OrientedPairsRejected;
            }
            public void Dispose()
            {
                // Each submission is short; unchecked subtraction also preserves
                // positive deltas when a lifetime int counter wraps after a long run.
                schedulerComponents+=unchecked(scheduler.ComponentBuilds-components);
                schedulerPlans+=unchecked(scheduler.PlanBuilds-plans);
                schedulerHits+=unchecked(scheduler.PlanCacheHits-hits);
                schedulerUploads+=unchecked(scheduler.FullMatrixUploads-uploads);
                schedulerMatrices+=unchecked(scheduler.FullMatrixElementsUploaded-matrices);
                schedulerPairs+=scheduler.BoundsPairTests-pairs;
                schedulerOverlaps+=unchecked(scheduler.ActualOverlaps-overlaps);
                schedulerUnions+=unchecked(scheduler.ComponentUnions-unions);
                schedulerOrientedTests+=unchecked(scheduler.OrientedPairTests-orientedTests);
                schedulerOrientedRejected+=unchecked(scheduler.OrientedPairsRejected-orientedRejected);
            }
        }
        public static SchedulerScope MeasureScheduler(SnowVehicleDrawScheduler scheduler) {return new SchedulerScope(scheduler);}
        public struct Scope : IDisposable
        {
            private readonly string name;
            private readonly long start;
            public Scope(string name) {this.name=name;start=Stopwatch.GetTimestamp();}
            public void Dispose(){Elapsed(name,Stopwatch.GetTimestamp()-start);}
        }
        public static Scope Measure(string name) {return new Scope(name);}
        // Composite work such as several native array uploads can aggregate its
        // stopwatch ticks before recording one sample for a camera submission.
        public static void Elapsed(string name,long ticks)
        {
            Counter counter;
            if(!counters.TryGetValue(name,out counter)){counter=new Counter();counters.Add(name,counter);}
            counter.Ticks+=ticks;counter.Maximum=Math.Max(counter.Maximum,ticks);counter.Count++;
        }
        public static void Frame(SeasonState state=null)
        {
            if(state!=null)seasonBits|=1<<(int)state.Current;
            var dt=Time.unscaledDeltaTime;
            // Do not discard stalls >= 1 second: those are exactly the frames
            // this diagnostic needs to expose when a client reports freezes.
            if(dt>0) {frames++;frameSeconds+=dt;maximumFrame=Math.Max(maximumFrame,dt);if(dt>=.1f)slowFrames++;}
            if(nextReport<=0) {nextReport=Time.realtimeSinceStartup+20f;lastCollections=GC.CollectionCount(0);}
            if(Time.realtimeSinceStartup<nextReport) return;
            var report=new StringBuilder("[DVSeasons] Performance: CPU submission ms mean/max; ");
            Counter renderCounter;
            double renderFrameMs=frames>0 && counters.TryGetValue("render-commands",out renderCounter)
                ?renderCounter.Ticks*(1000d/Stopwatch.Frequency)/frames:0;
            foreach(var pair in counters)
            {
                var c=pair.Value;double factor=1000d/Stopwatch.Frequency;
                report.Append(pair.Key).Append('=')
                    .Append((c.Count>0?c.Ticks*factor/c.Count:0).ToString("F2",CultureInfo.InvariantCulture)).Append('/')
                    .Append((c.Maximum*factor).ToString("F2",CultureInfo.InvariantCulture)).Append("; ");
                c.Ticks=c.Maximum=0;c.Count=0;
            }
            report.Append("whole-frame FPS=").Append((frameSeconds>0?frames/frameSeconds:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; max-frame-ms=").Append((maximumFrame*1000).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; frames>=100ms=").Append(slowFrames)
                .Append("; GC0=").Append(GC.CollectionCount(0)-lastCollections)
                .Append("; season-window=").Append(SeasonWindow())
                .Append("; window-frames/snow-submissions=").Append(frames).Append('/').Append(renderedFrames)
                .Append("; snow-record-ms-per-game-frame=").Append(renderFrameMs.ToString("F2",CultureInfo.InvariantCulture))
                .Append("; snow-culling(parts/lod-skipped/state-rejected/frustum-tests/lod-groups)=")
                .Append(CullingMean(cullingParts)).Append('/').Append(CullingMean(cullingLodSkipped))
                .Append('/').Append(CullingMean(cullingStateRejected)).Append('/').Append(CullingMean(cullingFrustumTests))
                .Append('/').Append(CullingMean(cullingLods))
                .Append("; snow-vehicle-draws(full/cheap)=")
                .Append((renderedFrames>0?detailedDraws/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append('/').Append((renderedFrames>0?exclusionDraws/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; snow-vehicle-commands=")
                .Append((renderedFrames>0?(detailedDraws+exclusionDraws-instancedExclusions+exclusionBatches-instancedFull+fullBatches+volumeCommands+distantCommands)/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; snow-native-materials/combined-commands/far-cars/far-commands=")
                .Append(RenderMean(nativeMaterialSlots)).Append('/').Append(RenderMean(combinedCommands))
                .Append('/').Append(RenderMean(distantCars)).Append('/').Append(RenderMean(distantCommands))
                .Append("; snow-native-sharing(shared-slots/private-slots/materials)=")
                .Append(NativeMean(nativeShared)).Append('/').Append(NativeMean(nativePrivate)).Append('/').Append(NativeMean(nativeVariants))
                .Append("; snow-capped-volumes(cars/commands)=")
                .Append((renderedFrames>0?volumeCars/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append('/').Append((renderedFrames>0?volumeCommands/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; snow-full-instancing(objects/batches)=")
                .Append((renderedFrames>0?instancedFull/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append('/').Append((renderedFrames>0?fullBatches/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; snow-full-batch-size(avg/max)=")
                .Append((fullBatches>0?instancedFull/(double)fullBatches:0).ToString("F2",CultureInfo.InvariantCulture))
                .Append('/').Append(fullBatchMaximum)
                .Append("; snow-ready-streams(avg/max)=")
                .Append((readyStreamSamples>0?readyStreamTotal/(double)readyStreamSamples:0).ToString("F2",CultureInfo.InvariantCulture))
                .Append('/').Append(readyStreamMaximum)
                .Append("; snow-active-graph(active-streams/active-edges/empty-streams/empty-bridge-edges)=")
                .Append(ActiveGraphMean(activeGraphStreams)).Append('/').Append(ActiveGraphMean(activeGraphEdges))
                .Append('/').Append(ActiveGraphMean(activeGraphEmptyStreams)).Append('/').Append(ActiveGraphMean(activeGraphEmptyEdges))
                .Append("; snow-exclusion-instancing(objects/batches)=")
                .Append((renderedFrames>0?instancedExclusions/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append('/').Append((renderedFrames>0?exclusionBatches/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; snow-scheduler-window(components/plans/cache-hits/matrix-uploads/matrices/pair-tests)=")
                .Append(schedulerComponents).Append('/').Append(schedulerPlans).Append('/').Append(schedulerHits)
                .Append('/').Append(schedulerUploads).Append('/').Append(schedulerMatrices).Append('/').Append(schedulerPairs)
                .Append("; snow-overlap-window(actual-overlaps/component-unions)=")
                .Append(schedulerOverlaps).Append('/').Append(schedulerUnions)
                .Append("; snow-obb-window(tests/rejected)=")
                .Append(schedulerOrientedTests).Append('/').Append(schedulerOrientedRejected)
                .Append("; snow-topology-window(pose-changes/envelope-expands/padding-grows/bound-publications)=")
                .Append(topologyPoses).Append('/').Append(topologyEnvelopes).Append('/').Append(topologyPadding).Append('/').Append(topologyPublications)
                .Append("; snow-scheduler-mode(part/vehicle-submissions)=").Append(partGraphSubmissions).Append('/').Append(vehicleGraphSubmissions)
                .Append("; snow-components(count-mean/largest-max/size-avg/multi-stream-mean)=")
                .Append(BatchingMean(batchingComponents)).Append('/').Append(batchingLargest)
                .Append('/').Append((batchingComponents>0?batchingVehicles/(double)batchingComponents:0).ToString("F2",CultureInfo.InvariantCulture))
                .Append('/').Append(BatchingMean(batchingMultiComponents))
                .Append("; snow-instancing-eligibility-full(eligible/unique-mesh/skinned/static/mirrored/streams/other/unique-key)=")
                .Append(BatchingMean(batchingEligible)).Append('/').Append(BatchingMean(batchingUniqueMesh))
                .Append('/').Append(BatchingMean(batchingSkinned)).Append('/').Append(BatchingMean(batchingStatic))
                .Append('/').Append(BatchingMean(batchingMirrored)).Append('/').Append(BatchingMean(batchingStreams))
                .Append('/').Append(BatchingMean(batchingOther))
                .Append('/').Append(BatchingMean(batchingUniqueKey))
                .Append("; snow-car-limit=").Append(snowLimit)
                .Append("; snow-resolution=").Append(renderWidth).Append('x').Append(renderHeight)
                .Append("; snow-sides(cars/depositing/sheltered/max)=").Append(sideSnowCars)
                .Append('/').Append(sideSnowDepositing).Append('/').Append(sideSnowSheltered)
                .Append('/').Append(sideSnowMaximum.ToString("F4",CultureInfo.InvariantCulture))
                .Append("; snow-sides-snowfall=").Append(sideSnowfall.ToString("F3",CultureInfo.InvariantCulture))
                .Append("; seasonal-readbacks(active/sync-total)=").Append(SeasonTextureReadback.ActiveRequests)
                .Append('/').Append(SeasonTextureReadback.SynchronousReadCount)
                .Append(". CPU scopes exclude GPU execution; FPS includes the whole game.");
            UnityEngine.Debug.Log(report.ToString());nextReport=Time.realtimeSinceStartup+20f;frames=0;frameSeconds=0;
            maximumFrame=0;slowFrames=0;lastCollections=GC.CollectionCount(0);
            detailedDraws=exclusionDraws=instancedExclusions=exclusionBatches=instancedFull=fullBatches=volumeCars=volumeCommands=0;renderedFrames=0;
            nativeMaterialSlots=combinedCommands=distantCars=distantCommands=0;
            nativeShared=nativePrivate=nativeVariants=0;nativeSamples=0;
            schedulerComponents=schedulerPlans=schedulerHits=schedulerUploads=schedulerMatrices=schedulerPairs=0;
            schedulerOverlaps=schedulerUnions=topologyPoses=topologyEnvelopes=topologyPadding=topologyPublications=0;
            schedulerOrientedTests=schedulerOrientedRejected=0;
            readyStreamSamples=readyStreamTotal=0;readyStreamMaximum=fullBatchMaximum=0;
            ResetActiveGraph();
            ResetBatching();
            ResetCulling();seasonBits=0;
        }
        private static string CullingMean(long total)
        {return (cullingSamples>0?total/(double)cullingSamples:0).ToString("F1",CultureInfo.InvariantCulture);}
        private static string RenderMean(long total)
        {return (renderedFrames>0?total/(double)renderedFrames:0).ToString("F1",CultureInfo.InvariantCulture);}
        private static void ResetCulling()
        {cullingParts=cullingLodSkipped=cullingStateRejected=cullingFrustumTests=cullingLods=0;cullingSamples=0;}
        private static string SeasonWindow()
        {
            if(seasonBits==0)return "unknown";
            var result=new StringBuilder();
            for(int season=0;season<4;season++)
                if((seasonBits&(1<<season))!=0){if(result.Length>0)result.Append('+');result.Append((SeasonKind)season);}
            return result.ToString();
        }
        private static string BatchingMean(long total)
        {return (batchingSamples>0?total/(double)batchingSamples:0).ToString("F1",CultureInfo.InvariantCulture);}
        private static string ActiveGraphMean(long total)
        {return (activeGraphSamples>0?total/(double)activeGraphSamples:0).ToString("F1",CultureInfo.InvariantCulture);}
        private static void ResetActiveGraph()
        {activeGraphStreams=activeGraphEdges=activeGraphEmptyStreams=activeGraphEmptyEdges=0;activeGraphSamples=0;partGraphSubmissions=vehicleGraphSubmissions=0;}
        private static void ResetBatching()
        {
            batchingComponents=batchingVehicles=batchingMultiComponents=0;
            batchingEligible=batchingUniqueMesh=batchingUniqueKey=batchingSkinned=batchingStatic=batchingMirrored=batchingStreams=batchingOther=0;
            batchingSamples=batchingLargest=0;
        }
        public static void Reset()
        {
            counters.Clear();nextReport=0;frames=0;frameSeconds=0;maximumFrame=0;slowFrames=0;lastCollections=0;
            detailedDraws=exclusionDraws=instancedExclusions=exclusionBatches=instancedFull=fullBatches=volumeCars=volumeCommands=0;
            nativeMaterialSlots=combinedCommands=distantCars=distantCommands=0;
            nativeShared=nativePrivate=nativeVariants=0;nativeSamples=0;
            schedulerComponents=schedulerPlans=schedulerHits=schedulerUploads=schedulerMatrices=schedulerPairs=0;
            schedulerOverlaps=schedulerUnions=topologyPoses=topologyEnvelopes=topologyPadding=topologyPublications=0;
            schedulerOrientedTests=schedulerOrientedRejected=0;
            readyStreamSamples=readyStreamTotal=0;readyStreamMaximum=fullBatchMaximum=0;
            ResetActiveGraph();
            ResetBatching();
            ResetCulling();seasonBits=0;
            renderedFrames=snowLimit=renderWidth=renderHeight=0;
            sideSnowCars=sideSnowDepositing=sideSnowSheltered=0;sideSnowMaximum=sideSnowfall=0f;
        }
    }
}
