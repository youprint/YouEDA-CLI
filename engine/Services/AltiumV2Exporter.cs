using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YouEDA.Engine.Models;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Eda.Primitives;

namespace YouEDA.Engine.Services;

/// <summary>Native PcbLib writer using the current OriginalCircuit.Altium source tree.</summary>
public sealed class AltiumV2Exporter
{
    /// <summary>Upserts one footprint into the shared, native Altium PcbLib.</summary>
    public async Task<string> UpsertPcbLibAsync(EdaComponent source, string outputDirectory, Downloaded3dModel? model = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "youeda.PcbLib");
        var library = await OpenSharedPcbLibAsync(outputDirectory);

        // Re-running a part refreshes it without duplicating its footprint. Other components,
        // their parameters, and their embedded STEP data remain untouched.
        Upsert(library, source, model);
        await library.SaveAsync(path);

        // Catch writer regressions before reporting a file as generated. This validates its compound
        // structure and all serialized primitive records with the same reader implementation.
        var verified = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(path);
        if (!verified.Contains(AltiumFootprintNaming.NameFor(source)) ||
            (model is not null && !verified.Models.Any(item => item.Name == model.FileName)))
            throw new InvalidDataException("The shared PcbLib did not pass post-write verification.");
        return path;
    }

    /// <summary>Loads the shared library once for high-throughput, single-writer batch exports.</summary>
    public async Task<PcbLibrary> OpenSharedPcbLibAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "youeda.PcbLib");
        if (File.Exists(path)) return (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(path);

        // A newly created V2 PcbLibrary can be internally round-tripped but some Altium releases
        // reject its default Library section. Seed from an Altium-authored library so its required
        // section metadata, layer mapping, and version information are preserved.
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "AltiumTemplate.PcbLib");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("The bundled Altium PcbLib template is missing.", templatePath);
        var library = (PcbLibrary)await AltiumLibrary.OpenPcbLibAsync(templatePath);
        foreach (var existing in library.Components.Select(item => item.Name).ToArray()) library.Remove(existing);
        library.ComponentParamsToc.Clear();
        library.Models.Clear();
        return library;
    }

    /// <summary>Mutates an already-open shared PcbLib. Caller controls checkpoint saves.</summary>
    public void Upsert(PcbLibrary library, EdaComponent source, Downloaded3dModel? model = null)
    {
        var footprintName = AltiumFootprintNaming.NameFor(source);
        // Build first: a failed conversion must not remove an earlier footprint from a
        // long-lived batch library that will later be checkpointed with other successes.
        var replacement = BuildComponent(source, footprintName, library, model).Build();
        // Remove an entry written by older YouEDA versions under its LCSC number when this part
        // is re-imported, then upsert the EasyEDA-named footprint. Identical package names are
        // shared library entries rather than duplicate per-part footprints.
        library.Remove(source.LcscPartNumber);
        library.Remove(footprintName);
        library.Add(replacement);
    }

    private static ComponentBuilder BuildComponent(EdaComponent source, string footprintName, PcbLibrary library, Downloaded3dModel? model)
    {
        var component = PcbComponent.Create(footprintName).WithDescription(source.Name);

        foreach (var pad in source.Pads)
        {
            var builder = PcbPad.Create(pad.Number);
            builder.At(Coord.FromMm(pad.Xmm), Coord.FromMm(pad.Ymm))
                    .Size(Coord.FromMm(pad.WidthMm), Coord.FromMm(pad.HeightMm))
                    .Shape(pad.Shape is "OVAL" or "ELLIPSE" ? PadShape.Round : PadShape.Rectangular)
                    .Rotation(pad.RotationDeg)
                    .WithDesignator(pad.Number);

                // A zero-hole pad must be emitted as an SMD pad; merely setting HoleSize(0)
                // leaves a partially configured pad record which Altium can reject on load.
                if (pad.HoleMm > 0)
                    builder.ThroughHole(Coord.FromMm(pad.HoleMm)).Plated(pad.Plated).Layer(74); // Multi-layer
                else
                    builder.Smd(MapCopperLayer(pad.Layer));
            var altiumPad = builder.Build();
            if (pad.SlotLengthMm > 0 && pad.HoleMm > 0)
            {
                // Altium stores slot shape/length only in the optional size/shape block.
                altiumPad.HasSizeShapeBlock = true;
                altiumPad.HoleType = PadHoleType.Slot;
                altiumPad.HoleSlotLength = Coord.FromMm(pad.SlotLengthMm).ToRaw();
            }
            component.AddPad(altiumPad);
        }

        foreach (var track in source.Shapes.Where(shape => shape.Kind == "TRACK" && shape.PointsMm.Count > 1))
        {
            for (var index = 1; index < track.PointsMm.Count; index++)
            {
                var start = track.PointsMm[index - 1];
                var end = track.PointsMm[index];
                component.AddTrack(trackBuilder => trackBuilder
                    .From(Coord.FromMm(start.X), Coord.FromMm(start.Y))
                    .To(Coord.FromMm(end.X), Coord.FromMm(end.Y))
                    .Width(Coord.FromMm(track.StrokeMm))
                    .Layer(MapEasyEdaLayer(track.Layer)));
            }
        }

        foreach (var arc in source.Shapes.Where(shape => shape.Kind == "ARC" && shape.PointsMm.Count > 0 && shape.RadiusMm > 0))
        {
            var center = arc.PointsMm[0];
            component.AddArc(arcBuilder => arcBuilder
                .Center(Coord.FromMm(center.X), Coord.FromMm(center.Y))
                .Radius(Coord.FromMm(arc.RadiusMm))
                .Angles(arc.StartAngleDeg, arc.EndAngleDeg)
                .Width(Coord.FromMm(arc.StrokeMm))
                .Layer(MapEasyEdaLayer(arc.Layer)));
        }

        // Preserve EasyEDA primitive artwork on its original semantic layer.  Passive
        // footprints commonly use these for silkscreen, paste/mask openings and
        // component-shape/lead-shape/marking artwork.
        foreach (var rect in source.Shapes.Where(shape => shape.Kind == "RECT" && shape.PointsMm.Count > 1))
        {
            var a = rect.PointsMm[0]; var z = rect.PointsMm[1];
            var corners = new[] { a, (z.X, a.Y), z, (a.X, z.Y), a };
            for (var i = 1; i < corners.Length; i++)
                component.AddTrack(trackBuilder => trackBuilder
                    .From(Coord.FromMm(corners[i - 1].X), Coord.FromMm(corners[i - 1].Y))
                    .To(Coord.FromMm(corners[i].X), Coord.FromMm(corners[i].Y))
                    .Width(Coord.FromMm(Math.Max(.01, rect.StrokeMm)))
                    .Layer(MapEasyEdaLayer(rect.Layer)));
        }
        foreach (var circle in source.Shapes.Where(shape => shape.Kind == "CIRCLE" && shape.PointsMm.Count > 1))
        {
            var center = circle.PointsMm[0];
            component.AddArc(arc => arc.Center(Coord.FromMm(center.X), Coord.FromMm(center.Y))
                .Radius(Coord.FromMm(circle.PointsMm[1].X)).FullCircle()
                .Width(Coord.FromMm(Math.Max(.01, circle.StrokeMm)))
                .Layer(MapEasyEdaLayer(circle.Layer)));
        }
        // EasyEDA's SOLIDREGION records include editor-only component, lead, paste,
        // and mask support layers.  EasyEDALoader does not turn these into extra
        // outline tracks; doing so added four non-source Mechanical-1 segments around
        // every passive footprint.  Keep the actual TRACK artwork above, and let
        // Altium generate mask/paste from the pads.

        if (model is not null) AddEasyEdaModel(component.Build(), library, model);

        return component;
    }

    /// <summary>Embeds an EasyEDA STEP model for both generated and source-library footprints.</summary>
    private static void AddEasyEdaModel(PcbComponent component, PcbLibrary library, Downloaded3dModel model)
    {
        // The footprint is normalised about (0,0), so the EasyEDA model origin is the same point.
        var modelId = Guid.TryParseExact(model.Source.Uuid, "N", out var easyEdaId)
            ? easyEdaId.ToString("B").ToUpperInvariant()
            : Guid.NewGuid().ToString("B").ToUpperInvariant();
        library.Models.RemoveAll(item => string.Equals(item.Id, modelId, StringComparison.OrdinalIgnoreCase));
        var embeddedModel = new PcbModel
        {
            Id = modelId, Name = model.FileName, IsEmbedded = true,
            ModelSource = "Undefined", StepData = model.StepData
        };
        embeddedModel.RecomputeChecksum();
        library.Models.Add(embeddedModel);

        var halfWidth = model.Source.WidthMm > 0 ? model.Source.WidthMm / 2 : 0.5;
        var halfHeight = model.Source.HeightMm > 0 ? model.Source.HeightMm / 2 : 0.5;
        var body = PcbComponentBody.Create()
            .OnLayer("MECHANICAL1")
            .WithName(model.FileName)
            // Altium treats a model-based body as a closed contour; an empty outline
            // is tolerated by readers but rejected by the native PcbLib editor.
            .Kind(0).ShapeBased(false).ModelId(modelId)
            .OverallHeight(Coord.FromMm(model.HeightMm))
            .AddPoint(Coord.FromMm(model.Source.Xmm - halfWidth), Coord.FromMm(model.Source.Ymm - halfHeight))
            .AddPoint(Coord.FromMm(model.Source.Xmm + halfWidth), Coord.FromMm(model.Source.Ymm - halfHeight))
            .AddPoint(Coord.FromMm(model.Source.Xmm + halfWidth), Coord.FromMm(model.Source.Ymm + halfHeight))
            .AddPoint(Coord.FromMm(model.Source.Xmm - halfWidth), Coord.FromMm(model.Source.Ymm + halfHeight))
            .At2D(Coord.FromMm(model.Source.Xmm), Coord.FromMm(model.Source.Ymm)).Rotation2D(0)
            .Rotation3D(model.Source.RotationXDeg, model.Source.RotationYDeg, model.Source.RotationZDeg)
            .OffsetZ(Coord.FromMm(model.Source.Zmm + model.ZOffsetMm))
            .Build();
        component.AddComponentBody(body);
    }

    private static int MapCopperLayer(string easyEdaLayer) => easyEdaLayer == "2" ||
        easyEdaLayer.Equals("BottomLayer", StringComparison.OrdinalIgnoreCase) ? 32 : 1;

    private static int MapEasyEdaLayer(string easyEdaLayer) => easyEdaLayer switch
    {
        "1" or "TopLayer" => 1,
        "2" or "BottomLayer" => 32,
        // Binary PcbLib layer IDs are not the visible layer numbers: IDs 2..31 are
        // Mid-Layer 1..30.  Top/Bottom Overlay are 33/34 respectively.  Using 21
        // accidentally placed silkscreen on Mid-Layer 20, which appears purple.
        "3" or "TopSilkLayer" => 33,
        "4" or "BottomSilkLayer" => 34,
        "5" or "TopPasteMaskLayer" => 35,
        "6" or "BottomPasteMaskLayer" => 36,
        "7" or "TopSolderMaskLayer" => 37,
        "8" or "BottomSolderMaskLayer" => 38,
        "10" or "BoardOutLine" => 57,
        "13" or "TopAssembly" => 57,
        "14" or "BottomAssembly" => 58,
        "15" or "Mechanical" => 59,
        // EasyEDA's library-only component artwork layers have no exact Altium
        // counterparts; retain them on separate mechanical layers instead of dropping
        // them or rendering them as copper.
        "99" or "ComponentShapeLayer" => 57,
        "100" or "LeadShapeLayer" => 58,
        // Match EasyEDALoader: component/pin marking is on Mechanical 11, not on the
        // Top Overlay silkscreen.  Otherwise a marker circle appears as an erroneous
        // extra yellow silkscreen dot beside passive pad 1.
        "101" or "ComponentMarkingLayer" => 67,
        _ => 33
    };
}
