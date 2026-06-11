using System;

namespace Magi.UnityTools.Core
{
    /// <summary>
    /// Lightweight Result helper (success/error) for init and runtime operations.
    /// Immutable; carries optional message/exception.
    /// </summary>
    public readonly struct Result
    {
        public bool IsSuccess { get; }
        public string Error { get; }
        public Exception Exception { get; }

        private Result(bool ok, string error, Exception ex)
        {
            IsSuccess = ok;
            Error = error;
            Exception = ex;
        }

        public static Result Success() => new Result(true, string.Empty, null);
        public static Result Fail(string error) => new Result(false, error ?? "Unknown error", null);
        public static Result Fail(Exception ex) => new Result(false, ex?.Message ?? "Unknown error", ex);

        public Result WithContext(string context)
        {
            if (IsSuccess) return this;
            var prefix = string.IsNullOrEmpty(context) ? string.Empty : $"{context}: ";
            return new Result(false, prefix + Error, Exception);
        }

        public override string ToString() => IsSuccess ? "OK" : $"Error: {Error}";
    }
}
