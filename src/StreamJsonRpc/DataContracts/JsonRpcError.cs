using Newtonsoft.Json;

namespace StreamJsonRpc
{
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class JsonRpcError
    {
        internal JsonRpcError(int code, string message) : this(code, message, data: null)
        {
        }

        [JsonConstructor]
        internal JsonRpcError(int code, string message, dynamic data)
        {
            this.Code = code;
            this.Message = message;
            this.Data = data;
        }

        [JsonProperty("code", Required = Required.Always)]
        internal int Code { get; private set; }

        [JsonProperty("message", Required = Required.Always)]
        internal string Message { get; private set; }

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        internal dynamic Data { get; private set; }

        public string ErrorStack => this.Data?.stack;

        public string ErrorCode =>this.Data?.code;
    }
}
