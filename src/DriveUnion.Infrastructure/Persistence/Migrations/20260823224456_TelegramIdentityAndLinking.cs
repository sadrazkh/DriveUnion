using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelegramIdentityAndLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveryStatus = table.Column<byte>(type: "smallint", nullable: false),
                    BlockedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramAccounts_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramBotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BotTokenProtected = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    BotUsername = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    BotUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramBotSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelegramLinkTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfirmationCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PresentedTelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    PresentedChatId = table.Column<long>(type: "bigint", nullable: true),
                    PresentedUsername = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PresentedDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PresentedLanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PresentedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramLinkTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramLinkTokens_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "TelegramBotSettings",
                columns: new[] { "Id", "BotTokenProtected", "BotUserId", "BotUsername", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 1, null, null, null, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAccounts_AppUserId",
                table: "TelegramAccounts",
                column: "AppUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramAccounts_TelegramUserId",
                table: "TelegramAccounts",
                column: "TelegramUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkTokens_AppUserId_ConsumedAt",
                table: "TelegramLinkTokens",
                columns: new[] { "AppUserId", "ConsumedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkTokens_TokenHash",
                table: "TelegramLinkTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramAccounts");

            migrationBuilder.DropTable(
                name: "TelegramBotSettings");

            migrationBuilder.DropTable(
                name: "TelegramLinkTokens");
        }
    }
}
