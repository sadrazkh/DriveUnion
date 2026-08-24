using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramTransportAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookPathSegmentProtected",
                table: "TelegramBotSettings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WebhookRegisteredAt",
                table: "TelegramBotSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretProtected",
                table: "TelegramBotSettings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TelegramFileIds",
                columns: table => new
                {
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotUserId = table.Column<long>(type: "bigint", nullable: false),
                    FileId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FileUniqueId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CachedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramFileIds", x => new { x.StoredFileId, x.BotUserId });
                    table.ForeignKey(
                        name: "FK_TelegramFileIds_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorDetail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentMessageId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelegramUpdatesSeen",
                columns: table => new
                {
                    UpdateId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUpdatesSeen", x => x.UpdateId);
                });

            migrationBuilder.UpdateData(
                table: "TelegramBotSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "WebhookPathSegmentProtected", "WebhookRegisteredAt", "WebhookSecretProtected" },
                values: new object[] { null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramOutbox_Status_CreatedAt",
                table: "TelegramOutbox",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramOutbox_TenantId_Status",
                table: "TelegramOutbox",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramFileIds");

            migrationBuilder.DropTable(
                name: "TelegramOutbox");

            migrationBuilder.DropTable(
                name: "TelegramUpdatesSeen");

            migrationBuilder.DropColumn(
                name: "WebhookPathSegmentProtected",
                table: "TelegramBotSettings");

            migrationBuilder.DropColumn(
                name: "WebhookRegisteredAt",
                table: "TelegramBotSettings");

            migrationBuilder.DropColumn(
                name: "WebhookSecretProtected",
                table: "TelegramBotSettings");
        }
    }
}
