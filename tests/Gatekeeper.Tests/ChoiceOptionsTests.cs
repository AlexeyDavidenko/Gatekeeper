using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the ChoiceOptions JSON (de)serialization helpers for question options and selected indices.
/// </summary>
public class ChoiceOptionsTests
{
    [Fact]
    public void SerializeQuestionOptions_With_Null_Returns_Null()
    {
        var result = ChoiceOptions.SerializeQuestionOptions(null);
        Assert.Null(result);
    }

    [Fact]
    public void SerializeQuestionOptions_With_Empty_List_Returns_Null()
    {
        var result = ChoiceOptions.SerializeQuestionOptions([]);
        Assert.Null(result);
    }

    [Fact]
    public void SerializeQuestionOptions_With_Options_Produces_Valid_Json()
    {
        var options = new[] { "Option A", "Option B", "Option C" };
        var serialized = ChoiceOptions.SerializeQuestionOptions(options);

        Assert.NotNull(serialized);
        Assert.Contains("Option A", serialized);
        Assert.Contains("Option B", serialized);
        Assert.Contains("Option C", serialized);
    }

    [Fact]
    public void SerializeQuestionOptions_And_ParseQuestionOptions_RoundTrip()
    {
        var originalOptions = new[] { "Apple", "Banana", "Cherry" };
        var serialized = ChoiceOptions.SerializeQuestionOptions(originalOptions);
        var deserialized = ChoiceOptions.ParseQuestionOptions(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(originalOptions, deserialized);
    }

    [Fact]
    public void ParseQuestionOptions_With_Null_Returns_Null()
    {
        var result = ChoiceOptions.ParseQuestionOptions(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseQuestionOptions_With_Empty_String_Returns_Null()
    {
        var result = ChoiceOptions.ParseQuestionOptions("");
        Assert.Null(result);
    }

    [Fact]
    public void SerializeSelectedIndices_Produces_Valid_Json()
    {
        var indices = new[] { 0, 2, 3 };
        var serialized = ChoiceOptions.SerializeSelectedIndices(indices);

        Assert.NotNull(serialized);
        Assert.Contains("0", serialized);
        Assert.Contains("2", serialized);
        Assert.Contains("3", serialized);
    }

    [Fact]
    public void SerializeSelectedIndices_And_ParseSelectedIndices_RoundTrip()
    {
        var originalIndices = new[] { 0, 2, 3 };
        var serialized = ChoiceOptions.SerializeSelectedIndices(originalIndices);
        var deserialized = ChoiceOptions.ParseSelectedIndices(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(originalIndices, deserialized);
    }

    [Fact]
    public void ParseSelectedIndices_With_Null_Returns_Empty_List()
    {
        var result = ChoiceOptions.ParseSelectedIndices(null);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseSelectedIndices_With_Empty_String_Returns_Empty_List()
    {
        var result = ChoiceOptions.ParseSelectedIndices("");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void SerializeAndParse_Empty_Indices_Array()
    {
        var originalIndices = Array.Empty<int>();
        var serialized = ChoiceOptions.SerializeSelectedIndices(originalIndices);
        var deserialized = ChoiceOptions.ParseSelectedIndices(serialized);

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized);
    }

    [Fact]
    public void ParseQuestionOptions_Preserves_Order()
    {
        var originalOptions = new[] { "Z", "A", "M", "B" };
        var serialized = ChoiceOptions.SerializeQuestionOptions(originalOptions);
        var deserialized = ChoiceOptions.ParseQuestionOptions(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(originalOptions, deserialized);
    }

    [Fact]
    public void ParseSelectedIndices_Preserves_Order_And_Duplicates()
    {
        var originalIndices = new[] { 3, 1, 3, 0 };
        var serialized = ChoiceOptions.SerializeSelectedIndices(originalIndices);
        var deserialized = ChoiceOptions.ParseSelectedIndices(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(originalIndices, deserialized);
    }
}
