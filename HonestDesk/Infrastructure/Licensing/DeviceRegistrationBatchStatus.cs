namespace HonestDesk.Infrastructure.Licensing
{
    public enum DeviceRegistrationBatchStatus
    {
        NewDevice,
        DuplicateRequest,
        AlreadyRegistered,
        UnknownClient,
        DeviceAssignedToAnotherClient
    }
}
