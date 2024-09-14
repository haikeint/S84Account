﻿using ACAPI.GraphQL.Middleware;
using ACAPI.Model;
using ACAPI.GraphQL.InputType;
using HotChocolate.Resolvers;
using Microsoft.EntityFrameworkCore;
using ACAPI.Data;
using ACAPI.Config;
using ACAPI.Helper;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Net;
using ACAPI.Service;
using DotNetEnv;
using StackExchange.Redis;
using System.Text.Json;
namespace ACAPI.GraphQL.Mutation
{
    public class AccountMutation : ObjectTypeExtension
    {
        protected override void Configure(IObjectTypeDescriptor descriptor)
        {
            descriptor.Name("Mutation");

            descriptor.Field("updateInfo")
                .Argument("objectInput", a => a.Type<NonNullType<UpdateInfoInputType>>())
                .Use<AuthorizedMiddleware>()
            .ResolveWith<Resolver>(res => res.UpdateInfo(default!));

            //descriptor.Field("updateSecure")
            //    .Argument("objectInput", arg => arg.Type<NonNullType<UpdateSecureInputType>>())
            //    .Use<AuthorizedMiddleware>()
            //    .ResolveWith<Resolver>(res => res.UpdateSecure(default!));

            descriptor.Field("sendVerifyEmail")
                .Use<AuthorizedMiddleware>()
                .ResolveWith<Resolver>(res => res.SendVerifyEmail(default!));

            descriptor.Field("changePassword")
                .Argument("oldPassword", arg => arg.Type<NonNullType<StringType>>())
                .Argument("newPassword", arg => arg.Type<NonNullType<StringType>>())
                .Use<AuthorizedMiddleware>()
                .ResolveWith<Resolver>(res => res.ChangePassword(default!));

            descriptor.Field("sendOtpToPhone")
                .Argument("newPhone", arg => arg.Type<NonNullType<StringType>>())
                .Use<AuthorizedMiddleware>()
                .ResolveWith<Resolver>(res => res.SendOTP2Phone(default!));

            descriptor.Field("verifyOtpToPhone")
                .Argument("newPhone", arg => arg.Type<NonNullType<StringType>>())
                .Argument("otp", arg => arg.Type<NonNullType<StringType>>())
                .Use<AuthorizedMiddleware>()
                .ResolveWith<Resolver>(res => res.VerifyOTP2Phone(default!));

            //descriptor.Field("changePhone")
            //    //.Argument("oldPhone", arg => arg.Type<StringType>())
            //    .Argument("newPhone", arg => arg.Type<NonNullType<StringType>>())
            //    .Use<AuthorizedMiddleware>()
            //    .ResolveWith<Resolver>(res => res.SendOTP2Phone(default!));

            descriptor.Field("changeEmail")
                .Argument("oldEmail", arg => arg.Type<StringType>())
                .Argument("newEmail", arg => arg.Type<NonNullType<StringType>>())
                .Use<AuthorizedMiddleware>()
                .ResolveWith<Resolver>(res => res.ChangeEmail(default!));
        }

        private class Resolver(
            IDbContextFactory<MysqlContext> contextFactory, 
            RedisConnectionPool redisConnectionPool,
            ViewRenderService viewRenderService)
        {
            private readonly IDbContextFactory<MysqlContext> _contextFactory = contextFactory;
            private readonly RedisConnectionPool _redisPool = redisConnectionPool;
            private readonly ViewRenderService _viewRenderService = viewRenderService;

            private readonly string REDIS_VERIFY = "verify_";
            private readonly string REDIS_VERIFY_PHONE = "verifyPhone_";

            public async Task<string> ChangePassword(IResolverContext ctx) { 
                string oldPassword = ctx.ArgumentValue<string>("oldPassword");
                string newPassword = ctx.ArgumentValue<string>("newPassword");

                long UserId = long.Parse(Util.GetContextData(ctx, EnvirConst.UserId));
                MysqlContext mysqlContext = _contextFactory.CreateDbContext();

                AccountModel? accountModel = await mysqlContext.Account
                    .Where(account => account.Id == UserId)
                    .Select(account => new AccountModel
                    {
                        Username = account.Username,
                        Password = account.Password,
                    })
                    .FirstOrDefaultAsync() ?? throw Util.Exception(HttpStatusCode.Forbidden, "Tài khoản không tồn tại.");
                
                if(!Password.Verify(oldPassword, accountModel.Password)) {
                    throw Util.Exception(HttpStatusCode.Forbidden, "Mật khẩu hiện tại không đúng.");
                }

                Task<int> rowAffect = mysqlContext.Database.ExecuteSqlRawAsync(
                    MysqlCommand.UPDATE_PASSWORD_BY_ID, 
                    Password.Hash(newPassword), 
                    UserId);
                if(!(await rowAffect > 0)) throw Util.Exception(HttpStatusCode.Forbidden, "Lỗi không xác định");

                PurgeRedis(accountModel.Username);
                return "Thay đổi mật khẩu thành công";
            } 
            public string SendOTP2Phone(IResolverContext ctx) { 
                string newPhone = ctx.ArgumentValue<string>("newPhone");
                if(newPhone.Length != 10 || !ValidatePhoneNumber(newPhone)) {
                    throw Util.Exception(HttpStatusCode.Forbidden, "Số điện thoại di động Việt Nam không hợp lệ");
                } 
                
                long UserId = long.Parse(Util.GetContextData(ctx, EnvirConst.UserId));

                string code = Util.RandomNumber(6);

                if(!SMS.Send(newPhone, $"Mã xác thực của bạn là: {Util.RandomNumber(6)}")) {
                    throw Util.Exception(HttpStatusCode.InternalServerError, "Lỗi gửi mã OTP vào điện thoại");
                }

                string redisKey = $"{REDIS_VERIFY_PHONE}_{UserId}_{newPhone}";
                
                TimeSpan? ttl = Redis.GetValue(_redisPool, redisCTX =>
                {
                    TimeSpan? ttl = redisCTX.KeyTimeToLive(redisKey);
                    if(ttl is not null) return ttl;

                    redisCTX.HashSet(redisKey, [
                            new HashEntry("OTP", code)
                        ]);
                    ttl = TimeSpan.FromMinutes(2);
                    redisCTX.KeyExpire(redisKey, ttl);
                    return null;
                });
         
                if(ttl is not null) throw Util.Exception(HttpStatusCode.Forbidden, $"Gửi lại mã OTP sau {ttl.Value.Seconds}s"); 
                
                return "Đã gửi mã OTP vào số điện thoại.";
            }

            public async Task<string> VerifyOTP2Phone(IResolverContext ctx) { 
                string newPhone = ctx.ArgumentValue<string>("newPhone");
                string otp = ctx.ArgumentValue<string>("otp");

                if(newPhone.Length != 10 || !ValidatePhoneNumber(newPhone)) {
                    throw Util.Exception(HttpStatusCode.Forbidden, "Số điện thoại di động Việt Nam không hợp lệ");
                } 
                
                long UserId = long.Parse(Util.GetContextData(ctx, EnvirConst.UserId));
                string redisKey = $"{REDIS_VERIFY_PHONE}_{UserId}_{newPhone}";

                RedisValue[] redisResult = Redis.GetValue(_redisPool, redisCTX =>
                {
                    return redisCTX.HashGet(redisKey, ["OTP"]);
                });

                if (!redisResult[0].HasValue) throw Util.Exception(HttpStatusCode.NotFound, "Mã OTP không tồn tại.");

                if(redisResult[0] != otp) throw Util.Exception(HttpStatusCode.Forbidden, "Mã OTP không đúng.");

                Redis.Handle(_redisPool, redisCTX =>
                {
                    redisCTX.KeyDelete(redisKey);
                });

                MysqlContext mysqlContext = _contextFactory.CreateDbContext();

                AccountModel? accountModel = await mysqlContext.Account
                    .Where(account => account.Id == UserId)
                    .Select(account => new AccountModel {
                        Username = account.Username,
                        Phone = account.Phone,
                    })
                    .FirstOrDefaultAsync() ?? throw Util.Exception(HttpStatusCode.Forbidden, "Tài khoản không tồn tại.");

                Task<int> rowAffect = mysqlContext.Database.ExecuteSqlRawAsync(
                    MysqlCommand.UPDATE_PHONE_BY_ID,
                    newPhone,
                    UserId);
                if (!(await rowAffect > 0)) throw Util.Exception(HttpStatusCode.Forbidden, "Lỗi không xác định");

                PurgeRedis(accountModel.Username);
                return "Đã thay đổi số điện thoại thành công.";
            }

            public async Task<string> ChangeEmail(IResolverContext ctx) { 
                string responeMessage = "";

                string oldEmail = ctx.ArgumentValue<string>("oldEmail");
                string newEmail = ctx.ArgumentValue<string>("newEmail");
                if(string.IsNullOrWhiteSpace(newEmail)) {
                    throw Util.Exception(HttpStatusCode.Forbidden, "Email không hợp lệ");
                }
                
                long UserId = long.Parse(Util.GetContextData(ctx, EnvirConst.UserId));

                MysqlContext mysqlContext = _contextFactory.CreateDbContext();

                AccountModel? accountModel = await mysqlContext.Account
                    .Where(account => account.Id == UserId)
                    .Select(account => new AccountModel
                    {
                        Email = account.Email,
                        Username = account.Username,
                        IsEmailVerified = account.IsEmailVerified,
                    })
                    .FirstOrDefaultAsync() ?? throw Util.Exception(HttpStatusCode.Forbidden, "Tài khoản không tồn tại.");
                
                if (string.IsNullOrWhiteSpace(accountModel.Email) 
                    || (!string.IsNullOrWhiteSpace(accountModel.Email) 
                    && accountModel.IsEmailVerified == false)) {

                        Task<int> rowAffect = mysqlContext.Database.ExecuteSqlRawAsync(
                            MysqlCommand.UPDATE_EMAIL_BY_ID, 
                            newEmail, 
                            UserId);
                        if(!(await rowAffect > 0)) throw Util.Exception(HttpStatusCode.Forbidden, "Lỗi không xác định");

                        PurgeRedis(accountModel.Username);
                        responeMessage = await SendVerifyEmail(ctx) 
                            ? "Vui lòng vào Email xác thực Email mới" 
                            : "Đã thay đổi Email nhưng chưa xác thực.";
                }

                if(!string.IsNullOrWhiteSpace(accountModel.Email) 
                    && accountModel.IsEmailVerified == true) {
                    if(accountModel.Email !=  oldEmail) throw Util.Exception(HttpStatusCode.Forbidden, "Email hiện tại không đúng.");
                    bool result = await SendMailForChangeEmail(
                        UserId, 
                        accountModel.Username ?? string.Empty, 
                        accountModel.Email, 
                        newEmail);
                    responeMessage = "Kiểm tra hòm thư Email để xác nhận việc thay đổi Email";
                }

                return responeMessage;
            }
            public async Task<bool> SendVerifyEmail(IResolverContext ctx) {
                long UserId = long.Parse(Util.GetContextData(ctx, EnvirConst.UserId));
                string redisKey = $"{REDIS_VERIFY}{UserId}";

                TimeSpan? ttl = Redis.GetValue(_redisPool, redisCTX => redisCTX.KeyTimeToLive(redisKey));

                if(ttl is not null) {
                    throw Util.Exception(HttpStatusCode.Forbidden, $"Gửi lại mã xác thực sau {ttl.Value.Minutes} phút");
                }

                MysqlContext mysqlContext = _contextFactory.CreateDbContext();
                AccountModel accountModel = await mysqlContext.Account
                    .Where(acc => acc.Id == UserId)
                    .Select(acc => new AccountModel {
                        Email = acc.Email,
                        Username = acc.Username,
                    }).FirstOrDefaultAsync() ?? throw Util.Exception(HttpStatusCode.NotFound); 
                
                if(accountModel.Email is null) {
                    throw Util.Exception(HttpStatusCode.Forbidden, "Tài khoản chưa có Email"); 
                }

                if(accountModel.IsEmailVerified is not null && (bool)accountModel.IsEmailVerified) {
                    throw Util.Exception(HttpStatusCode.Forbidden, "Tài khoản đã xác thực Email");
                }
                
                var payload = new {
                    Id = UserId,
                    Email= accountModel.Email ?? string.Empty,
                    Operator = "VerifyEmail"
                };
               
                string jwtToken = JWT.GenerateES384(
                    JsonSerializer.Serialize(payload),
                    JWT.ISSUER,
                    (ctx.Service<IHttpContextAccessor>()).HttpContext?.Request.Host.ToString() ?? string.Empty, 
                    DateTime.UtcNow.AddMinutes(30));

                Redis.Handle(_redisPool, redisCTX =>
                {
                    redisCTX.HashSet(redisKey, [
                        new HashEntry("Token", jwtToken),
                        ]);
                    redisCTX.KeyExpire(redisKey, TimeSpan.FromMinutes(30));
                });

                string verifyLink = $"https://localhost:5000/api/auth/verify-email?token={jwtToken}";

                string emailBody = await _viewRenderService.RenderToStringAsync(
                    "~/View/VerifyEmailTempalte.cshtml",
                    new {
                        UrlStatic = Env.GetString("URL_STATIC"),
                        Username = accountModel.Username ?? string.Empty,
                        Text = "xác thực Email",
                        ButtonHTML = "Xác thực Email ngay",
                        Expire = 30,
                        VerifyLink = verifyLink
                    });
                return Mail.Send(accountModel.Email ?? string.Empty, "Xác thực Email tại HBPlay", emailBody);
            }

            public bool UpdateInfo(IResolverContext ctx)
            {
                long UserId = long.Parse(Util.GetContextData(ctx, EnvirConst.UserId));

                UpdateInfoInput updateInfoInput = ctx.ArgumentValue<UpdateInfoInput>("objectInput");

                AccountModel accountInput = new()
                {
                    Id = UserId,
                    Address = updateInfoInput.Address is null ? null : updateInfoInput.Address,
                    Birthdate = updateInfoInput.Birthdate is null ? null : updateInfoInput.Birthdate,
                    Fullname = updateInfoInput.Fullname is null ? null : updateInfoInput.Fullname,
                    Gender = updateInfoInput.Gender is null ? null : updateInfoInput.Gender,
                };

                MysqlContext mysqlContext = _contextFactory.CreateDbContext();
                mysqlContext.Account.Attach(accountInput);
                foreach (PropertyEntry property in mysqlContext.Entry(accountInput).Properties)
                {
                    property.IsModified = property.CurrentValue is not null
                        && property.Metadata.Name != "Id";
                }
                return mysqlContext.SaveChanges() > 0;
            }

            private void PurgeRedis(string? username)
            {
                if (username is null) return;

                Redis.Handle(_redisPool, redisContext =>
                {
                    redisContext.KeyDelete(username);
                });
            }

            private async Task<bool> SendMailForChangeEmail(long UserId, string Username ,string oldEmail, string newEmail) {

                string redisKey = $"{REDIS_VERIFY}{UserId}";
                TimeSpan? ttl = Redis.GetValue(_redisPool, redisCTX => redisCTX.KeyTimeToLive(redisKey));

                if(ttl is not null) {
                    throw Util.Exception(HttpStatusCode.Forbidden, $"Thực hiện lại thao tác sau {ttl.Value.Minutes} phút");
                }

                var payload = new {
                    Id = UserId,
                    Email= oldEmail,
                    NewEmail = newEmail,
                    Operator = "RequestChangeEmail"
                };
                
                string jwtToken = JWT.GenerateES384(
                    JsonSerializer.Serialize(payload),
                    JWT.ISSUER,
                    Env.GetString("HOST"), 
                    DateTime.UtcNow.AddMinutes(30));

                Redis.Handle(_redisPool, redisCTX =>
                {
                    redisCTX.HashSet(redisKey, [
                        new HashEntry("Token", jwtToken),
                        ]);
                    redisCTX.KeyExpire(redisKey, TimeSpan.FromMinutes(30));
                });

                string verifyLink = $"https://localhost:5000/api/auth/verify-email?token={jwtToken}";

                string emailBody = await _viewRenderService.RenderToStringAsync(
                    "~/View/VerifyEmailTempalte.cshtml",
                    new {
                        UrlStatic = Env.GetString("URL_STATIC"),
                        Username = Username ?? string.Empty,
                        Text = "thay đổi Email",
                        ButtonHTML = "Thay đổi Email ngay",
                        Expire = 30,
                        VerifyLink = verifyLink
                    });
                return Mail.Send(oldEmail ?? string.Empty, "Yêu cầu thay đổi Email tại HBPlay", emailBody);
            }

            private static readonly List<string> PHONE_NUMBER_VALID = [];
            private bool ValidatePhoneNumber(string phoneNumber) {
                if(PHONE_NUMBER_VALID.Count.Equals(0)) {
                    MysqlContext mysqlContext = _contextFactory.CreateDbContext();
                    IEnumerable<PhonePrefixModel> phonePrefixModel = mysqlContext.PhonePrefix
                        .Select(prefix => new PhonePrefixModel {
                            Prefix = prefix.Prefix
                        });
                    foreach (PhonePrefixModel item in phonePrefixModel.ToList()) {
                        if (item.Prefix is not null) PHONE_NUMBER_VALID.Add(item.Prefix);
                    }
                }
                return PHONE_NUMBER_VALID.Contains(phoneNumber[..3]);
            }
        }
    }
}
