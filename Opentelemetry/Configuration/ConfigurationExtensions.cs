using Microsoft.Extensions.Configuration;

namespace CFX.OpenTelemetry.Configuration
{
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Gets a configuration value by key, searching across all sections if not found at root level.
        /// Supports both direct keys ("InstanceId") and section-qualified keys ("CommonSettings:InstanceId").
        /// </summary>
        /// <param name="configuration">The configuration instance</param>
        /// <param name="key">The key to search for (with or without section prefix)</param>
        /// <returns>The configuration value or null if not found</returns>
        public static string? GetValueByKey(this IConfiguration configuration, string? key)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            
            if(string.IsNullOrEmpty(key))
            {
                return null;
            }

            // First, try direct lookup (handles both "InstanceId" and "CommonSettings:InstanceId")
            string? value = configuration[key];
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            // If not found and key doesn't contain a section separator, search all sections
            if (!key.Contains(':'))
            {
                return SearchAllSections(configuration, key);
            }

            return null;
        }

        private static string? SearchAllSections(IConfiguration configuration, string key)
        {
            foreach (IConfigurationSection section in configuration.GetChildren())
            {
                // Check if the key exists directly in this section
                string? value = section[key];
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }

                // Recursively search nested sections
                value = SearchAllSections(section, key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
