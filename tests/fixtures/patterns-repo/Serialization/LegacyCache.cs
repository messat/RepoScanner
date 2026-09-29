using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Web.UI;
using Newtonsoft.Json;

namespace PatternsRepo.Serialization
{
    // CP002: insecure deserialization. Insecure on purpose: this file is never built.
    public class LegacyCache
    {
        public object ReadBinary(Stream stream)
        {
            BinaryFormatter formatter = new BinaryFormatter(); // CP002 (CA2300)
            return formatter.Deserialize(stream);
        }

        public object ReadViewState(string text)
        {
            return new LosFormatter().Deserialize(text); // CP002 (CA2305)
        }

        public object ReadContract(Stream stream)
        {
            return new NetDataContractSerializer().Deserialize(stream); // CP002 (CA2310)
        }

        public object ReadState(string text)
        {
            return new ObjectStateFormatter().Deserialize(text); // CP002 (CA2315)
        }

        public T ReadJson<T>(string json)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto // CP002 (CA2326)
            };
            return JsonConvert.DeserializeObject<T>(json, settings);
        }

        // Not a finding: None is the safe setting
        public T ReadJsonSafely<T>(string json)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
            return JsonConvert.DeserializeObject<T>(json, settings);
        }
    }

    public class CachedMessage
    {
        [JsonProperty(TypeNameHandling = TypeNameHandling.All)] // CP002 (CA2326)
        public object Payload { get; set; }
    }
}
