namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Reads SQL text back into the model, or says exactly why it can't.</summary>
internal interface ISqlReader
{
    ReadResult Read(string sql);
}
