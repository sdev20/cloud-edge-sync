namespace Edge.WebAPI.Requests;

public record InstrumentUpdateRequest(Guid InstrumentId, string Name, string Description, string Status);