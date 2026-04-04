namespace Common.Model
{
    public class ResponseBase
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ResponseBase<T>: ResponseBase
    {
        public T? Result { get; set; }
    }
}