namespace HonestDesk.Infrastructure.Licensing
{
    public enum DeviceRegistrationBatchStatus
    {
        NewDevice,
        AddressUpdate,
        DuplicateRequest,
        AlreadyRegistered,
        UnknownClient,
        DeviceAssignedToAnotherClient
    }
}
