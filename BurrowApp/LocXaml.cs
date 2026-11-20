using System;
using System.Windows.Markup;

namespace Burrow
{
    /// <summary>XAML: Text="{b:T some.key}" shows the translated string.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension() { }
        public TExtension(string key) { Key = key; }

        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
    }
}
