FROM mcr.microsoft.com/dotnet/aspnet:10.0

ARG APP
WORKDIR /app
COPY ${APP}/ ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    APP_DLL=${APP}.dll \
    TZ=UTC

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
