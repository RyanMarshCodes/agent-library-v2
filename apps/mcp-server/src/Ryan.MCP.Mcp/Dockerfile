FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY Ryan.MCP.slnx ./
COPY src/Ryan.MCP.Mcp/Ryan.MCP.Mcp.csproj src/Ryan.MCP.Mcp/
RUN dotnet restore src/Ryan.MCP.Mcp/Ryan.MCP.Mcp.csproj

COPY src/Ryan.MCP.Mcp/ src/Ryan.MCP.Mcp/
RUN dotnet publish src/Ryan.MCP.Mcp/Ryan.MCP.Mcp.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8081
EXPOSE 8081

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Ryan.MCP.Mcp.dll"]
