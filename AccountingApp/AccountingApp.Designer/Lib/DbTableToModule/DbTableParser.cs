using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;

namespace AccountingApp.Designer.Lib.DbTableToModule
{
    public static class DbTableParser
    {
        public static string Import(DesignerEnvironment designerEnvironment, string dataSourceName, List<DbTableDefinition> tables)
        {
            var modules = new List<string>();
            var err = new List<string>();
            foreach (var table in tables)
            {
                var module = new ModuleDesign()
                {
                    Name = DbNameToDesignName(table.Name),
                    DataSourceName = dataSourceName,
                    DbTable = table.Name
                };
                foreach (var col in table.Columns)
                {
                    var field = CreateFieldDesign(col);
                    // Id カラムは大文字小文字を無視して IdFieldDesign 化される(下記 CreateFieldDesign)。
                    // フレームワークがシステム Id として認識するよう、名前は正規の "Id" に揃える
                    // (実際の DB 列名は col.Name のまま下行で DbColumn にマッピングされる)。
                    field.Name = field is IdFieldDesign
                        ? SystemFieldNames.Id
                        : AvoidUnintendedSystemFieldName(field, DbNameToDesignName(col.Name));
                    field.GetType().GetProperties().Where(e => e.GetCustomAttribute<DbColumnAttribute>() != null).FirstOrDefault()?.SetValue(field, col.Name);
                    module.Fields.Add(field);
                }
                module.CreateLayouts();

                try
                {
                    File.WriteAllTextAsync(Path.Combine(designerEnvironment.CurrentFileDirectory, "Modules", $"{module.Name}.mod.json"), JsonConverterEx.SerializeObject(module));
                    modules.Add(module.Name);
                }
                catch (Exception exp)
                {
                    err.Add(exp.Message);
                }
            }

            //ページフレームに追加
            var designData = designerEnvironment.GetDesignData();
            var pageFrame = designData.PageFrames.Find("Main");
            if (pageFrame == null) pageFrame = designData.PageFrames.ToList().FirstOrDefault();
            if (pageFrame == null) return string.Empty;
            foreach (var module in modules)
            {
                if (pageFrame.Left.Links.Any(e => e.Module == module)) continue;
                pageFrame.Left.Links.Add(new PageLink
                {
                    Module = module,
                    Title = module,
                });
            }
            var path = Path.Combine(designerEnvironment.CurrentFileDirectory, "PageFrames", $"{pageFrame.Name}.frm.json");
            try
            {
                File.WriteAllText(path, JsonConverterEx.SerializeObject(pageFrame));
            }
            catch (Exception exp)
            {
                err.Add(exp.Message);
            }
            return string.Join(Environment.NewLine, err);
        }

        static string DbNameToDesignName(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;

            var words = source.Split(['_', '.']);
            if (words.Length == 1)
            {
                if (string.IsNullOrEmpty(words[0])) return string.Empty;
                var first = CultureInfo.InvariantCulture.TextInfo.ToUpper(words[0].Substring(0, 1));
                if (words[0].Length == 1) return first;
                return first + words[0].Substring(1);
            }

            var capitalizedWords = words.Select(word =>
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word.ToLower()));
            return string.Concat(capitalizedWords);
        }

        // 予約システムフィールド名は「正しい型」のときだけ許可する(その型なら正規のシステムフィールドで、
        // フレームワークの自動挙動が正しく働く)。型が違うのは偶然の名前衝突なので、末尾に _ を付けて
        // 自動論理削除 / 楽観ロック / 監査列の自動セット等が意図せず発動するのを防ぐ。
        // 期待する型はコアの ModuleDesignExtensions のシステムフィールド生成と揃える。
        static string AvoidUnintendedSystemFieldName(FieldDesignBase field, string name)
        {
            var isLegitimate = name switch
            {
                SystemFieldNames.Id => field is IdFieldDesign,
                SystemFieldNames.LogicalDelete => field is BooleanFieldDesign,
                SystemFieldNames.CreatedAt or SystemFieldNames.UpdatedAt or SystemFieldNames.DeletedAt => field is DateTimeFieldDesign,
                SystemFieldNames.Creator or SystemFieldNames.Updater or SystemFieldNames.Deleter => field is LinkFieldDesign,
                SystemFieldNames.OptimisticLocking => field is OptimisticLockingFieldDesign,
                _ => true,
            };
            return isLegitimate ? name : name + "_";
        }

        static FieldDesignBase CreateFieldDesign(DbColumnDefinition col)
        {
            if (col.Name.ToLower() == SystemFieldNames.Id.ToLower()) return new IdFieldDesign();

            var field = CreateFieldByNetType(col.NetTypeFullName);

            // NOT NULL 列は必須にする。
            if (field is ValueFieldDesignBase valueField)
                valueField.IsRequired = !col.IsNullable;

            return field;
        }

        static FieldDesignBase CreateFieldByNetType(string netTypeFullName)
        {
            var integerTypes = new[]
            {
                typeof(byte).FullName!,
                typeof(short).FullName!,
                typeof(ushort).FullName!,
                typeof(int).FullName!,
                typeof(uint).FullName!,
                typeof(long).FullName!,
                typeof(ulong).FullName!,
            };
            var realTypes = new[]
            {
                typeof(Single).FullName!,
                typeof(double).FullName!,
                typeof(decimal).FullName!,
            };

            // 整数列は小数入力を許さない(MaxFractionDigits=0)。
            if (integerTypes.Contains(netTypeFullName)) return new NumberFieldDesign { MaxFractionDigits = 0 };
            if (realTypes.Contains(netTypeFullName)) return new NumberFieldDesign();
            if (typeof(bool).FullName! == netTypeFullName) return new BooleanFieldDesign();
            if (typeof(DateTime).FullName! == netTypeFullName ||
                typeof(DateTimeOffset).FullName! == netTypeFullName) return new DateTimeFieldDesign();
            if (typeof(DateOnly).FullName! == netTypeFullName) return new DateFieldDesign();
            if (typeof(TimeOnly).FullName! == netTypeFullName ||
                typeof(TimeSpan).FullName! == netTypeFullName) return new TimeFieldDesign();

            // Guid / char / string / その他 → テキスト。
            return new TextFieldDesign();
        }
    }
}
