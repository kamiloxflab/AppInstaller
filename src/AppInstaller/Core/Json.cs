using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AppInstaller.Core
{
    /// <summary>JSON bez zewnętrznych bibliotek (DataContractJsonSerializer jest w .NET Framework).</summary>
    public static class Json
    {
        static DataContractJsonSerializer Ser<T>()
        {
            return new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        }

        public static T Parse<T>(byte[] data)
        {
            // Pomiń BOM UTF-8
            int off = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            using (var ms = new MemoryStream(data, off, data.Length - off))
                return (T)Ser<T>().ReadObject(ms);
        }

        public static T Parse<T>(string text)
        {
            return Parse<T>(Encoding.UTF8.GetBytes(text));
        }

        public static string Write<T>(T obj)
        {
            using (var ms = new MemoryStream())
            {
                Ser<T>().WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
