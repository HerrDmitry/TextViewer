namespace TextViewer.Services;

/// <summary>
/// Discriminated union: either success value or error value.
/// </summary>
public readonly struct Result<T, E>
{
    private readonly T? _value;
    private readonly E? _error;
    public bool IsSuccess { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result is error");
    public E Error => !IsSuccess ? _error! : throw new InvalidOperationException("Result is success");

    private Result(T value) { _value = value; _error = default; IsSuccess = true; }
    private Result(E error, bool _) { _value = default; _error = error; IsSuccess = false; }

    public static Result<T, E> Success(T value) => new(value);
    public static Result<T, E> Failure(E error) => new(error, false);
}
