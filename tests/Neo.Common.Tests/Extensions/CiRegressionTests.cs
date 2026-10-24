using System.Globalization;
using System.Text;
using Neo.Common.Extensions;
using Neo.Common.Utility;
using Xunit;

namespace Neo.Common.Tests.Extensions;

public class CiRegressionTests
{
    [Theory]
    [InlineData("XML", "xml")]
    [InlineData("XMLHttpRequest", "xml-http-request")]
    [InlineData("getURL", "get-url")]
    [InlineData("HTTP2Server", "http2-server")]
    [InlineData("already-kebab", "already-kebab")]
    public void KebabCase_PreservesAcronyms(string input, string expected) => Assert.Equal(expected, input.ToKebabCase());

    [Fact]
    public void KebabCase_IsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("xml-id", "XMLId".ToKebabCase());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(123.5, 123)]
    [InlineData(-123.5, -123)]
    [InlineData("123.5", 123)]
    [InlineData("-123.5", -123)]
    public void NumericConversion_TruncatesTowardZero(object value, int expected)
    {
        Assert.Equal(expected, value.Int());
        Assert.Equal((long)expected, value.Long());
    }

    [Fact]
    public void NumericConversion_RejectsOverflowAndNonfiniteValues()
    {
        Assert.Equal(0, long.MaxValue.Int());
        Assert.Equal(0, double.NaN.Int());
        Assert.Equal(0L, double.PositiveInfinity.Long());
        Assert.Equal(2147483648L, "2147483648".Long());
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775808", 0L)]
    [InlineData("-9223372036854775809", 0L)]
    [InlineData("2.147483648e9", 2147483648L)]
    [InlineData("not-a-number", 0L)]
    public void LongConversion_DoesNotLoseStringPrecision(string input, long expected) => Assert.Equal(expected, input.Long());

    [Fact]
    public void NumericConversion_PreservesDecimalPrecisionAndCurrentCulture()
    {
        Assert.Equal(long.MaxValue, ((decimal)long.MaxValue).Long());
        Assert.Equal(int.MaxValue, ((decimal)int.MaxValue + 0.9m).Int());
        Assert.Equal(0, ((decimal)int.MaxValue + 1m).Int());
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(1, "1,9".Int());
            Assert.Equal(-1L, "-1,9".Long());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void GenericDefaultDetection_IsNotStringEmptiness()
    {
        Assert.False(TypeExtensions.IsNullOrDefault(""));
        Assert.False(TypeExtensions.IsNullOrDefault(" "));
        Assert.True(TypeExtensions.IsNullOrDefault<string?>(null));
        Assert.True(TypeExtensions.IsNullOrDefault<object>(0));
        Assert.False(TypeExtensions.IsNullOrDefault<int?>(0));
    }

    [Theory]
    [InlineData("hELLo wORLD", true, "HelloWorld")]
    [InlineData("hELLo wORLD", false, "HELLoWORLD")]
    [InlineData("customer entity", false, "CustomerEntity")]
    public void PascalCase_DoesNotAbbreviateTableNames(string input, bool lower, string expected) => Assert.Equal(expected, input.ToPascalCase(lower));

    [Fact]
    public void DictionaryMapping_ConvertsTypesAndSkipsNonwritableMembers()
    {
        var id = Guid.NewGuid();
        var result = new Dictionary<string, string>
        {
            ["Age"] = "30", ["Id"] = id.ToString(), ["Count"] = "", ["Amount"] = "1.5",
            ["Enabled"] = "true", ["State"] = "Active", ["Name"] = "changed",
            ["Item"] = "changed", ["StaticValue"] = "changed", ["Missing"] = "ignored",
            ["ReadOnlyField"] = "changed", ["ReadOnlyProperty"] = "changed"
        }.ToObject<Mapped>();
        Assert.Equal(30, result.Age);
        Assert.Equal(id, result.Id);
        Assert.Null(result.Count);
        Assert.Equal(1.5m, result.Amount);
        Assert.True(result.Enabled);
        Assert.Equal(MappingState.Active, result.State);
        Assert.Equal("unchanged", result.Name);
        Assert.Equal("unchanged", result.ReadOnlyField);
        Assert.Equal("unchanged", result.ReadOnlyProperty);
        Assert.Equal("unchanged", Mapped.StaticValue);
    }

    [Fact]
    public void DictionaryMapping_InvalidValuesAreNotSilentlyDefaulted()
    {
        Assert.Throws<ArgumentException>(() => new Dictionary<string, string> { ["Age"] = "invalid" }.ToObject<Mapped>());
    }

    [Fact]
    public void DictionaryMapping_IsInvariantAndRetainsStringValues()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = new Dictionary<string, string> { ["Amount"] = "1.5", ["Text"] = "", ["Boxed"] = "unchanged" }.ToObject<Mapped>();
            Assert.Equal(1.5m, result.Amount);
            Assert.Equal("", result.Text);
            Assert.Equal("unchanged", result.Boxed);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("%PDF-1.4", "application/pdf")]
    [InlineData("%!PS-Adobe-3.0", "application/postscript")]
    [InlineData("ordinary text", "text/plain")]
    [InlineData("", "application/octet-stream")]
    public void MimeDetection_KnownSignaturesPrecedeTextHeuristic(string input, string expected) => Assert.Equal(expected, NeoMimeTypes.GetMimeType(Encoding.ASCII.GetBytes(input)));

    public enum MappingState { Inactive, Active }

    public class Mapped
    {
        public int Age = 0;
        public decimal Amount = 0;
        public Guid Id { get; set; }
        public int? Count { get; set; }
        public bool Enabled { get; set; }
        public MappingState State { get; set; }
        public string Name { get; private set; } = "unchanged";
        public readonly string ReadOnlyField = "unchanged";
        public string ReadOnlyProperty => "unchanged";
        public string Text { get; set; } = "unchanged";
        public object? Boxed { get; set; }
        public static string StaticValue { get; set; } = "unchanged";
        public string this[int index] { get => "unchanged"; set => throw new InvalidOperationException(); }
    }
}
