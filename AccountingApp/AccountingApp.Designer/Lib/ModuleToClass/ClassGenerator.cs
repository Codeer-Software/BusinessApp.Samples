using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository.Design;
using System.Text;

namespace AccountingApp.Designer.Lib.ModuleToClass
{
    internal class ClassGenerator
    {
        internal static string ModuleDesignToDataFieldClass(ModuleDesign mod)
            => GenerateModuleClass(mod, field => field.CreateData()?.GetType());

        internal static string ModuleDesignToFieldClass(ModuleDesign mod)
            => GenerateModuleClass(mod, field => field.CreateField()?.GetType());

        static string GenerateModuleClass(ModuleDesign mod, Func<FieldDesignBase, Type?> resolveType)
        {
            var properties = new List<(string TypeName, string FieldName)>();

            foreach (var field in mod.Fields)
            {
                if (IsLinkField(field.Name)) continue;

                var type = resolveType(field);
                if (type == null) continue;

                properties.Add((type.Name, field.Name));
            }

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"public class {mod.Name}");
            stringBuilder.AppendLine("{");
            foreach (var (typeName, fieldName) in properties)
                stringBuilder.AppendLine($"    public {typeName}? {fieldName} {{ get; set; }}");
            stringBuilder.AppendLine("}");

            return stringBuilder.ToString();
        }
        private static bool IsLinkField(string fieldName)
        {
            var nameObj = new FieldName(fieldName);
            return nameObj.IsLink;
        }
    }
}
