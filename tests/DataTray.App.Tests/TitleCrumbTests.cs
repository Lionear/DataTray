using DataTray.App.ViewModels;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

public class TitleCrumbTests
{
    [Fact] // SE-291: the title bar shows connection › database › object, and a grouping node is not an object.
    public void Object_crumb_names_objects_only()
    {
        Assert.Equal("customers", MainViewModel.ObjectCrumb(DbNodeKind.Table, "customers", "Sales"));
        Assert.Equal("sp_rebuild", MainViewModel.ObjectCrumb(DbNodeKind.Procedure, "sp_rebuild", "Sales"));

        // Folders, schemas and cosmetic groups are where you are, not what you are looking at.
        Assert.Equal("", MainViewModel.ObjectCrumb(DbNodeKind.TableFolder, "Tables", "Sales"));
        Assert.Equal("", MainViewModel.ObjectCrumb(DbNodeKind.ColumnFolder, "Columns", "Sales"));
        Assert.Equal("", MainViewModel.ObjectCrumb(DbNodeKind.Schema, "dbo", "Sales"));
        Assert.Equal("", MainViewModel.ObjectCrumb(DbNodeKind.Group, "Security", "Sales"));

        // The database already sits in the crumb; "Sales › Sales" says nothing twice.
        Assert.Equal("", MainViewModel.ObjectCrumb(DbNodeKind.Database, "Sales", "Sales"));

        // Nothing selected, nothing to say — which is what keeps the dash off the bar.
        Assert.Equal("", MainViewModel.ObjectCrumb(null, "customers", "Sales"));
        Assert.Equal("", MainViewModel.ObjectCrumb(DbNodeKind.Table, "", "Sales"));
    }
}
