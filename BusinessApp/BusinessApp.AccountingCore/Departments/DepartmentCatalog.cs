namespace BusinessApp.AccountingCore.Departments;

/// <summary>読み込み済みの部門。</summary>
public sealed class DepartmentCatalog
{
    private readonly IReadOnlyDictionary<DepartmentId, DepartmentDefinition> _byId;

    public DepartmentCatalog(IEnumerable<DepartmentDefinition> departments)
    {
        ArgumentNullException.ThrowIfNull(departments);
        _byId = departments.ToDictionary(d => d.Id);
    }

    public DepartmentDefinition? Find(DepartmentId departmentId)
        => _byId.TryGetValue(departmentId, out var department) ? department : null;
}
