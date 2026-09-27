namespace ApplicationTesting
{
    using Microsoft.AspNetCore.Identity;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using RunDelta.API.DTO;
    using RunDelta.API.Identity.Persistence;
    using RunDelta.API.Services;
    using RunDelta.API.Services.Email;
    using SendGrid;
    using SendGrid.Helpers.Mail;
    using System.Net;
    using System.Text.Json;

    [TestClass]
    public sealed class SendGridEmailServiceTests
    {
        private const string RawToken = "CfDJ8N+raw/token==with+specials";
        private const string BootstrapPassword = "Bootstrap!Common#Pw123";
        private const string TemplateId = "d-driver-template";
        private const string ForgotTemplateId = "d-forgot-template";
        private const string CreateUrlBase = "http://localhost:5173/reset-password";

        private Mock<ISendGridClient> _client = null!;
        private Mock<UserManager<User>> _userManager = null!;
        private SendGridEmailOptions _options = null!;
        private CapturingLogger _logger = null!;
        private User _user = null!;
        private RegisterDto _request = null!;

        [TestInitialize]
        public void Setup()
        {
            _client = new Mock<ISendGridClient>(MockBehavior.Strict);

            var store = new Mock<IUserStore<User>>();
            _userManager = new Mock<UserManager<User>>(
                store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            _options = new SendGridEmailOptions
            {
                SenderEmail = "noreply@rundelta.ai",
                SenderName = "RunDelta",
                DriverInvitationTemplateId = TemplateId,
                ForgotPasswordTemplateId = ForgotTemplateId,
                CreatePasswordUrlBase = CreateUrlBase,
                ResetPasswordUrlBase = CreateUrlBase
            };

            _user = new User
            {
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Email = "driver@example.com",
                UserName = "driver@example.com"
            };

            _request = new RegisterDto { Email = _user.Email, Password = BootstrapPassword };
            _logger = new CapturingLogger();

            _userManager
                .Setup(m => m.GeneratePasswordResetTokenAsync(_user))
                .ReturnsAsync(RawToken);
        }

        private SendGridEmailService CreateSut() =>
            new(
                _client.Object,
                _userManager.Object,
                Options.Create(_options),
                new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
                _logger);

        private SendGridMessage? _sent;

        private void ArrangeAccepted()
        {
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => _sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));
        }

        // ---------- Spec items 9, 10, 11, 13, 16 ----------

        [TestMethod]
        public async Task SendDriverInvitation_BootstrapPasswordNeverAppearsInMessage()
        {
            ArrangeAccepted();

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            var json = _sent!.Serialize();
            Assert.IsFalse(json.Contains(BootstrapPassword));
            Assert.IsFalse(json.Contains(RawToken));
            Assert.IsFalse(string.Join("\n", _logger.Messages).Contains(BootstrapPassword));
        }

        [TestMethod]
        public async Task SendDriverInvitation_RawAndEncodedTokenAreNotLogged()
        {
            ArrangeAccepted();

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            var logs = string.Join("\n", _logger.Messages);
            Assert.IsTrue(_logger.Messages.Count > 0, "Expected at least one log entry.");
            Assert.IsFalse(logs.Contains(RawToken));
            Assert.IsFalse(logs.Contains(SendGridEmailService.EncodeToken(RawToken)));
        }

        [TestMethod]
        public async Task SendDriverInvitation_CreatePasswordUrlIsNotLogged()
        {
            ArrangeAccepted();

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            var data = (SendGridEmailService.PasswordLinkTemplateData)_sent!.Personalizations[0].TemplateData;
            var logs = string.Join("\n", _logger.Messages);
            Assert.IsFalse(logs.Contains(data.createPasswordUrl));
            Assert.IsFalse(logs.Contains(CreateUrlBase));
        }

        [TestMethod]
        public async Task SendDriverInvitation_TemplateData_ContainsOnlyFourKeys()
        {
            ArrangeAccepted();

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            using var doc = JsonDocument.Parse(_sent!.Serialize());
            var templateData = doc.RootElement
                .GetProperty("personalizations")[0]
                .GetProperty("dynamic_template_data");

            var keys = templateData.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
            CollectionAssert.AreEqual(
                new[] { "applicationName", "createPasswordUrl", "currentYear", "recipientEmail" },
                keys);
        }

        [TestMethod]
        public async Task SendDriverInvitation_PropagatesCancellationToken()
        {
            using var cts = new CancellationTokenSource();
            CancellationToken observed = default;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((_, ct) => observed = ct)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request, cts.Token);

            Assert.AreEqual(cts.Token, observed);
        }

        [TestMethod]
        public async Task SendDriverInvitation_UsesSuppliedUser_NoLookup()
        {
            ArrangeAccepted();

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            _userManager.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
            _userManager.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
            _userManager.Verify(m => m.GeneratePasswordResetTokenAsync(_user), Times.Once);
        }

        // ---------- Token encoding ----------

        [TestMethod]
        public void EncodeToken_ProducesUrlSafeString()
        {
            var encoded = SendGridEmailService.EncodeToken(RawToken);

            Assert.IsFalse(encoded.Contains('+'));
            Assert.IsFalse(encoded.Contains('/'));
            Assert.IsFalse(encoded.Contains('='));
        }

        [TestMethod]
        public void DecodeToken_RoundTripsEncodedToken()
        {
            var encoded = SendGridEmailService.EncodeToken(RawToken);

            Assert.AreEqual(RawToken, SendGridEmailService.DecodeToken(encoded));
        }

        [TestMethod]
        public void DecodeToken_InvalidInput_ThrowsFormatException()
        {
            Assert.ThrowsExactly<FormatException>(() => SendGridEmailService.DecodeToken("%%%not-base64url"));
        }

        // ---------- SendDriverInvitationAsync ----------

        [TestMethod]
        public async Task SendDriverInvitation_Success_ReturnsOk()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            var result = await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.IsTrue(result.Succeeded, string.Join("; ", result.Errors));
            Assert.IsNotNull(sent);
            _client.Verify(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task SendDriverInvitation_GeneratesIdentityResetToken()
        {
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            _userManager.Verify(m => m.GeneratePasswordResetTokenAsync(_user), Times.Once);
        }

        [TestMethod]
        public async Task SendDriverInvitation_UsesConfiguredTemplateId()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.AreEqual(TemplateId, sent!.TemplateId);
        }

        [TestMethod]
        public async Task SendDriverInvitation_SendsToUserEmail_FromConfiguredSender()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.AreEqual(_user.Email, sent!.Personalizations[0].Tos[0].Email);
            Assert.AreEqual(_options.SenderEmail, sent.From.Email);
            Assert.AreEqual(_options.SenderName, sent.From.Name);
        }

        [TestMethod]
        public async Task SendDriverInvitation_TemplateData_ContainsExpectedKeys()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            var data = (SendGridEmailService.PasswordLinkTemplateData)sent!.Personalizations[0].TemplateData;
            Assert.AreEqual(_user.Email, data.recipientEmail);
            Assert.AreEqual("RunDelta", data.applicationName);
            Assert.AreEqual(2026, data.currentYear);
            Assert.IsFalse(string.IsNullOrWhiteSpace(data.createPasswordUrl));
        }

        [TestMethod]
        public async Task SendDriverInvitation_Url_UsesConfiguredBaseAndUserId()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            var data = (SendGridEmailService.PasswordLinkTemplateData)sent!.Personalizations[0].TemplateData;
            var uri = new Uri(data.createPasswordUrl);

            Assert.AreEqual(CreateUrlBase, uri.GetLeftPart(UriPartial.Path));
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Assert.AreEqual(_user.Id.ToString(), query["userId"]);
        }

        [TestMethod]
        public async Task SendDriverInvitation_Url_CodeIsBase64UrlEncodedToken()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            await CreateSut().SendDriverInvitationAsync(_user, _request);

            var data = (SendGridEmailService.PasswordLinkTemplateData)sent!.Personalizations[0].TemplateData;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(data.createPasswordUrl).Query);
            var code = query["code"]!;

            Assert.AreEqual(SendGridEmailService.EncodeToken(RawToken), code);
            Assert.AreEqual(RawToken, SendGridEmailService.DecodeToken(code));
        }

        [TestMethod]
        public async Task SendDriverInvitation_SendGridRejects_ReturnsFail()
        {
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Response(HttpStatusCode.Forbidden, new StringContent("{\"errors\":[]}"), null));

            var result = await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Errors[0].Contains("403"));
        }

        [TestMethod]
        public async Task SendDriverInvitation_ClientThrows_ReturnsFailWithoutThrowing()
        {
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("network down"));

            var result = await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("Email could not be sent.", result.Errors[0]);
        }

        [TestMethod]
        public async Task SendDriverInvitation_MissingTemplateId_FailsWithoutCallingSendGrid()
        {
            _options.DriverInvitationTemplateId = "";

            var result = await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.IsFalse(result.Succeeded);
            _client.Verify(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()), Times.Never);
            _userManager.Verify(m => m.GeneratePasswordResetTokenAsync(It.IsAny<User>()), Times.Never);
        }

        [TestMethod]
        public async Task SendDriverInvitation_MissingUrlBase_FailsWithoutCallingSendGrid()
        {
            _options.CreatePasswordUrlBase = "";

            var result = await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.IsFalse(result.Succeeded);
            _client.Verify(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task SendDriverInvitation_UserWithoutEmail_FailsWithoutCallingSendGrid()
        {
            _user.Email = null;

            var result = await CreateSut().SendDriverInvitationAsync(_user, _request);

            Assert.IsFalse(result.Succeeded);
            _client.Verify(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ---------- SendEmailAsync (plain) ----------

        [TestMethod]
        public async Task SendEmail_Plain_SendsHtmlWithoutTemplate()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            var result = await CreateSut().SendEmailAsync("x@y.com", "Hi", "<p>Body</p>", "X");

            Assert.IsTrue(result.Succeeded);
            Assert.IsNull(sent!.TemplateId);
            Assert.AreEqual("Hi", sent.Subject);
            Assert.AreEqual("<p>Body</p>", sent.HtmlContent);
            Assert.AreEqual("x@y.com", sent.Personalizations[0].Tos[0].Email);
        }

        [TestMethod]
        public async Task SendEmail_MissingRecipient_Fails()
        {
            var result = await CreateSut().SendEmailAsync("", "Hi", "<p/>");

            Assert.IsFalse(result.Succeeded);
            _client.Verify(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ---------- SendForgotPasswordAsync ----------

        [TestMethod]
        public async Task SendForgotPassword_UsesForgotTemplateAndEncodedCode()
        {
            SendGridMessage? sent = null;
            _client
                .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                .Callback<SendGridMessage, CancellationToken>((m, _) => sent = m)
                .ReturnsAsync(new Response(HttpStatusCode.Accepted, new StringContent("{}"), null));

            var result = await CreateSut().SendForgotPasswordAsync(_user);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(ForgotTemplateId, sent!.TemplateId);
            var data = (SendGridEmailService.PasswordLinkTemplateData)sent.Personalizations[0].TemplateData;
            var code = System.Web.HttpUtility.ParseQueryString(new Uri(data.createPasswordUrl).Query)["code"]!;
            Assert.AreEqual(RawToken, SendGridEmailService.DecodeToken(code));
        }

        private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private sealed class CapturingLogger : ILogger<SendGridEmailService>
        {
            public List<string> Messages { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Messages.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
            }
        }
    }
}
