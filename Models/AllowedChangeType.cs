[Flags]
public enum AllowedChangeType
{
    None = 0,
    Add = 1,
    Modify = 2,
    Remove = 4,

    Any = Add | Modify | Remove
}