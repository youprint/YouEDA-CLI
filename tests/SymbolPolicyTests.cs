using YouEDA.Engine.Models;
using YouEDA.Engine.Services;

namespace YouEDA.Engine.Tests;

/// <summary>
/// Deterministic, offline tests of the ported symbol-correctness policies against the exact
/// parts YouEDA's AGENTS.md documents as reviewed identities. These construct EdaComponent
/// fixtures directly rather than fetching live EasyEDA data, so they stay fast and reproducible
/// in CI while still proving the ported logic enforces the documented behavior — never guessed
/// from pin count or package name alone.
/// </summary>
public class SymbolPolicyTests
{
    private static EdaSymbolPin Pin(string number, string name) => new(number, name, ElectricalType: 0, RotationDeg: 0, NameAnchor: "");

    [Fact]
    public void EasyEdaSymbolPreference_PinsTheSevenConfirmedLdoExceptions()
    {
        // AGENTS.md: "The seven confirmed selections are retained even with sparse metadata."
        foreach (var part in new[] { "C14289", "C5446", "C58069", "C6186", "C6187", "C71136", "C3113" })
        {
            var component = new EdaComponent { LcscPartNumber = part };
            Assert.Equal("confirmed per-part EasyEDA choice", EasyEdaSymbolPreference.Reason(component));
        }
    }

    [Fact]
    public void EasyEdaSymbolPreference_DoesNotApplyToAnUnrelatedPart()
    {
        var component = new EdaComponent { LcscPartNumber = "C11702", Description = "0402 resistor" };
        Assert.Null(EasyEdaSymbolPreference.Reason(component));
    }

    [Fact]
    public void NativeCommonSymbolPolicy_ResolvesC16133AsPolarisedCapacitorWithPin1Positive()
    {
        // AGENTS.md: "C16133/TAJB107K006RNJ ... native Polarised Capacitor, pin 1 positive, pin 2 negative."
        var component = new EdaComponent { LcscPartNumber = "C16133", Description = "10uF tantalum capacitor" };
        component.Properties["Manufacturer Part"] = "TAJB107K006RNJ";
        component.SymbolPins.Add(Pin("1", "1"));
        component.SymbolPins.Add(Pin("2", "2"));

        var binding = NativeCommonSymbolPolicy.Resolve(component);

        Assert.NotNull(binding);
        Assert.Equal("polarised capacitor", binding!.Kind);
        Assert.Equal(["1", "2"], binding.Numbers);
        Assert.Equal(["+", "-"], binding.Names);
    }

    [Fact]
    public void NativeCommonSymbolPolicy_RejectsUnverifiedMpnForC16133()
    {
        // Numeric-only pins require the exact reviewed MPN; an unverified part must not be guessed.
        var component = new EdaComponent { LcscPartNumber = "C16133", Description = "10uF tantalum capacitor" };
        component.Properties["Manufacturer Part"] = "SOME-OTHER-TANTALUM-PART";
        component.SymbolPins.Add(Pin("1", "1"));
        component.SymbolPins.Add(Pin("2", "2"));

        Assert.Null(NativeCommonSymbolPolicy.Resolve(component));
    }

    [Fact]
    public void NativeCommonSymbolPolicy_ResolvesC109227AsOptoisolatorWithReviewedPinMapping()
    {
        // AGENTS.md: "Reuse native Optoisolator automatically for reviewed C109227/LTV-817S-TA1-C ... 1=A, 2=K, 3=E, 4=C."
        var component = new EdaComponent { LcscPartNumber = "C109227", Description = "Optoisolator phototransistor output" };
        component.Properties["Manufacturer Part"] = "LTV-817S-TA1-C";
        component.SymbolPins.Add(Pin("1", "1"));
        component.SymbolPins.Add(Pin("2", "2"));
        component.SymbolPins.Add(Pin("3", "3"));
        component.SymbolPins.Add(Pin("4", "4"));

        var binding = NativeCommonSymbolPolicy.Resolve(component);

        Assert.NotNull(binding);
        Assert.Equal("optoisolator", binding!.Kind);
        Assert.Equal(["A", "K", "E", "C"], binding.Names);
    }

    [Fact]
    public void TransistorSymbolPolicy_ResolvesC9634AsNpnWithNamedPins()
    {
        var component = new EdaComponent { LcscPartNumber = "C9634", Description = "NPN transistor" };
        component.Properties["Manufacturer Part"] = "D882(RANGE:160-320)";
        component.SymbolPins.Add(Pin("1", "B"));
        component.SymbolPins.Add(Pin("2", "C"));
        component.SymbolPins.Add(Pin("3", "E"));

        var binding = TransistorSymbolPolicy.Resolve(component);

        Assert.NotNull(binding);
        Assert.Equal("NPN", binding!.Kind);
        Assert.Equal("1", binding.Control);  // B
        Assert.Equal("3", binding.Common);   // E
        Assert.Equal("2", binding.Output);   // C
    }

    [Fact]
    public void TransistorSymbolPolicy_RejectsConflictingPolarityEvenWithProfileMatch()
    {
        // "Reject known polarity/function conflicts" — description claims PNP but the profile is NPN.
        var component = new EdaComponent { LcscPartNumber = "C9634", Description = "PNP transistor" };
        component.Properties["Manufacturer Part"] = "D882(RANGE:160-320)";
        component.SymbolPins.Add(Pin("1", "B"));
        component.SymbolPins.Add(Pin("2", "C"));
        component.SymbolPins.Add(Pin("3", "E"));

        Assert.Null(TransistorSymbolPolicy.Resolve(component));
    }

    [Fact]
    public void DiscreteNetworkSymbolPolicy_ResolvesC2488AsMb10sBridgeNotGenericDiodePair()
    {
        // AGENTS.md: "PSM712 is not a common-cathode diode pair" / MB10S retains +, -, AC1, AC2.
        var component = new EdaComponent { LcscPartNumber = "C2488", Description = "MB10S bridge rectifier" };
        component.Properties["Manufacturer Part"] = "MB10S-50MIL";
        component.SymbolPins.Add(Pin("1", "+"));
        component.SymbolPins.Add(Pin("2", "-"));
        component.SymbolPins.Add(Pin("3", "C2"));
        component.SymbolPins.Add(Pin("4", "C1"));

        var binding = DiscreteNetworkSymbolPolicy.Resolve(component);

        Assert.NotNull(binding);
        Assert.Equal("bridge", binding!.Kind);
        Assert.Equal(["+", "-", "AC2", "AC1"], binding.Roles);
    }
}
