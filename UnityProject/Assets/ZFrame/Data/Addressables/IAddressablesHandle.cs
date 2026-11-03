namespace ZFrame.Data.Addressables
{
    /// <summary>
    /// Opaque token for one underlying Addressables handle, returned by
    /// <see cref="IAddressablesAdapter.LoadAsync{T}"/> and handed back to
    /// <see cref="IAddressablesAdapter.Release"/>. Exists purely so EditMode
    /// tests can substitute a fake for the UnityEngine.Addressables static
    /// API — implementations wrap whatever native state they need (the real
    /// adapter wraps an <c>AsyncOperationHandle&lt;Object&gt;</c>).
    /// </summary>
    public interface IAddressablesHandle
    {
    }
}
