namespace Neo.Common.Extensions.Examples;

/// <summary>
/// Examples demonstrating the usage of GetAttribute extension methods.
/// </summary>
public class GetAttributeExamples
{
    // Example attributes
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public class CustomAttribute : Attribute
    {
        public string Value { get; set; } = string.Empty;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public class AnotherAttribute : Attribute
    {
        public int Number { get; set; }
    }

    // Example interface with attribute
    [Custom(Value = "Interface Attribute")]
    public interface IExampleInterface
    {
        void DoSomething();
    }

    // Example base class with attribute
    [Another(Number = 42)]
    public abstract class BaseExampleClass
    {
        public abstract void DoWork();
    }

    // Example implementation class
    public class ExampleImplementation : BaseExampleClass, IExampleInterface
    {
        public override void DoWork() { }
        public void DoSomething() { }
    }

    // Example class with its own attribute
    [Custom(Value = "Class Attribute")]
    public class ExampleClass : IExampleInterface
    {
        public void DoSomething() { }
    }

    /// <summary>
    /// Demonstrates how to use GetAttribute method.
    /// </summary>
    public static void DemonstrateGetAttribute()
    {
        // Example 1: Get attribute from interface
        var interfaceAttr = typeof(IExampleInterface).GetAttribute<CustomAttribute>();
        Console.WriteLine($"Interface attribute: {interfaceAttr?.Value}"); // "Interface Attribute"

        // Example 2: Get attribute from implementation class (finds interface attribute)
        var implAttr = typeof(ExampleImplementation).GetAttribute<CustomAttribute>();
        Console.WriteLine($"Implementation attribute: {implAttr?.Value}"); // "Interface Attribute"

        // Example 3: Get attribute from base class
        var baseAttr = typeof(ExampleImplementation).GetAttribute<AnotherAttribute>();
        Console.WriteLine($"Base class attribute: {baseAttr?.Number}"); // 42

        // Example 4: Get attribute from class with its own attribute
        var classAttr = typeof(ExampleClass).GetAttribute<CustomAttribute>();
        Console.WriteLine($"Class attribute: {classAttr?.Value}"); // "Class Attribute"

        // Example 5: Get attribute with deep search (includes base types)
        var deepAttr = typeof(ExampleImplementation).GetAttributeDeep<CustomAttribute>(includeBaseTypes: true);
        Console.WriteLine($"Deep search attribute: {deepAttr?.Value}"); // "Interface Attribute"

        // Example 6: Get attribute that doesn't exist
        var nonExistentAttr = typeof(ExampleImplementation).GetAttribute<CustomAttribute>();
        Console.WriteLine($"Non-existent attribute: {nonExistentAttr?.Value}"); // null
    }
}
