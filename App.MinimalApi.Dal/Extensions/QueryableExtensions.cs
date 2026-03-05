namespace App.MinimalApi.Dal.Extensions
{
    public static class QueryableExtensions
    {
        /// <summary>
        /// Applies a sequence of string-based Include(...) calls to the query.
        /// Safe no-op when <paramref name="includes"/> is null or empty.
        /// </summary>
        public static IQueryable<T> IncludeMultiple<T>(this IQueryable<T> query, string[] includes) where T : class
        {
            if (query == null) return query;
            if (includes == null || includes.Length == 0) return query;

            foreach (var include in includes)
            {
                if (string.IsNullOrWhiteSpace(include)) continue;
                query = query.Include(include);
            }

            return query;
        }
    }
}
