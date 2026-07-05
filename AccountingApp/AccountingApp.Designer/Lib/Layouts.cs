using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository.Design;

namespace AccountingApp.Designer.Lib
{
    internal static class Layouts
    {
        // UI に出さない制御用システム項目(Id 同様に詳細・一覧から除外)。
        static readonly string[] HiddenSystemFieldNames =
            [SystemFieldNames.LogicalDelete, SystemFieldNames.OptimisticLocking, SystemFieldNames.DeletedAt, SystemFieldNames.Deleter];

        // フレームワークが自動セットする監査項目(編集させず ViewOnly で表示)。
        static readonly string[] AutoManagedSystemFieldNames =
            [SystemFieldNames.CreatedAt, SystemFieldNames.UpdatedAt, SystemFieldNames.Creator, SystemFieldNames.Updater];

        static bool IsHiddenSystemField(FieldDesignBase field)
            => field is OptimisticLockingFieldDesign || HiddenSystemFieldNames.Contains(field.Name);

        static bool IsAutoManagedSystemField(FieldDesignBase field)
            => AutoManagedSystemFieldNames.Contains(field.Name);

        internal static void CreateLayouts(this ModuleDesign module)
        {
            var defaultLayout = module.DetailLayouts[""];
            defaultLayout.Layout = new GridLayoutDesign();
            var listLayout = module.ListLayouts[""];
            listLayout.Elements.Clear();
            listLayout.Elements.Add(new());

            defaultLayout.AddHeaderLayout(module);
            foreach (var field in module.Fields.ToList())
            {
                if (field is IdFieldDesign) continue;
                else if (field is LabelFieldDesign) continue;
                else if (field is RadioButtonFieldDesign) continue;
                // 制御用システム項目(LogicalDelete/OptimisticLocking/DeletedAt/Deleter)は詳細・一覧とも出さない。
                else if (IsHiddenSystemField(field)) continue;
                else if (field is RadioGroupFieldDesign radio)
                {
                    defaultLayout.AddRadioGroup(module, radio);
                }
                else if (field is ListFieldDesign list)
                {
                    defaultLayout.AddList(list);
                    continue;
                }
                else
                {
                    // 監査項目(CreatedAt/UpdatedAt/Creator/Updater)は自動セットなので編集させず ViewOnly で出す。
                    defaultLayout.AddField(module, field, IsAutoManagedSystemField(field));
                }
                if (field is LinkFieldDesign link)
                {
                    if (string.IsNullOrEmpty(link.DisplayTextVariable)) continue;
                    if (link.DisplayTextVariable == "Id.Value") continue;
                }
                // 複数行テキストは一覧の列にすると横長で崩れやすいので一覧には出さない(詳細には出す)。
                if (field is TextFieldDesign { IsMultiline: true }) continue;
                if (!string.IsNullOrEmpty(module.DataSourceName))
                {
                    listLayout.Elements[0].Add(new() { FieldName = field.Name });
                }
            }
            if (!listLayout.Elements[0].Any()) listLayout.Elements[0].Add(new());

            defaultLayout.AddSubmitLayout(module);
        }

        private static void AddHeaderLayout(this DetailLayoutDesign design, ModuleDesign module)
        {
            var field = new LabelFieldDesign()
            {
                Name = "Header",
                Text = module.Name,
                Style = LabelStyle.H1
            };
            module.Fields.Add(field);

            ((GridLayoutDesign)design.Layout).Rows.Add(new GridRow
            {
                Columns = [new GridColumn
                {
                    Layout = new FieldLayoutDesign("Header"),
                    HorizontalAlignment = HorizontalAlignment.Center
                }]
            });
        }

        private static void AddSubmitLayout(this DetailLayoutDesign design, ModuleDesign module)
        {
            if (string.IsNullOrEmpty(module.DataSourceName)) return;

            var field = new SubmitButtonFieldDesign
            {
                Name = "Submit"
            };
            module.Fields.Add(field);

            ((GridLayoutDesign)design.Layout).Rows.Add(new GridRow
            {
                Columns = [new GridColumn
                {
                    Layout = new FieldLayoutDesign("Submit"),
                    HorizontalAlignment = HorizontalAlignment.Center
                }]
            });
        }

        private static void AddField(this DetailLayoutDesign design, ModuleDesign module, FieldDesignBase field, bool viewOnly = false)
        {
            var labelField = new LabelFieldDesign()
            {
                Name = field.Name + "Label",
                Text = string.Empty,
                RelativeField = field.Name
            };
            module.Fields.Add(labelField);

            ((GridLayoutDesign)design.Layout).Rows.Add(new GridRow
            {
                Columns = [new GridColumn
                {
                    Layout = new FieldLayoutDesign(field.Name + "Label"),
                    Width = 150,
                    VerticalAlignment = VerticalAlignment.Middle
                }, new GridColumn()
                {
                    Layout = new FieldLayoutDesign(field.Name) { IsViewOnly = viewOnly ? true : null },
                }]
            });
        }

        private static void AddRadioGroup(this DetailLayoutDesign design, ModuleDesign module, RadioGroupFieldDesign radio)
        {
            var field = new LabelFieldDesign()
            {
                Name = radio.Name + "Label",
                Text = radio.Name
            };
            module.Fields.Add(field);
            design.DataOnlyFields.Add(radio.Name);

            var radioButtonCount = module.Fields.OfType<RadioButtonFieldDesign>().Where(e => e.GroupField == radio.Name).Count();

            ((GridLayoutDesign)design.Layout).Rows.Add(new GridRow
            {
                Columns =
                [
                    new GridColumn
                    {
                        Layout = new FieldLayoutDesign(radio.Name + "Label"),
                        Width = 150,
                        VerticalAlignment = VerticalAlignment.Middle
                    },
                    new GridColumn
                    {
                        Layout = new GridLayoutDesign
                        {
                            IsFlowLayout = true,
                            Rows =
                            [
                                new GridRow
                                {
                                    Columns = Enumerable.Range(0, radioButtonCount).Select(i => new GridColumn
                                    {
                                        Layout = new FieldLayoutDesign(radio.Name + "Item" + i)
                                    }).ToList()
                                }
                            ]
                        }
                    }
                ]
            });
        }

        private static void AddList(this DetailLayoutDesign design, ListFieldDesign list)
        {
            ((GridLayoutDesign)design.Layout).Rows.Add(new GridRow
            {
                Columns = [new GridColumn
                {
                    Layout = new FieldLayoutDesign(list.Name),
                }]
            });
        }

    }
}
