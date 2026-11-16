namespace App.MinimalApi.Service.Extensions
{
    public static class StringExtensions
    {
        /// <summary>Cuts a value to a column length (audit and configuration metadata).</summary>
        public static string? Truncate(this string? value, int maxLength) =>
            value is not null && value.Length > maxLength ? value[..maxLength] : value;
    }
}
