var builder = DistributedApplication.CreateBuilder(args);

var payments = builder.AddProject<Projects.Payments_Api>("payments-api");

builder.AddProject<Projects.Orders_Api>("orders-api")
    .WithReference(payments);

builder.Build().Run();
