﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BTCPayServer.Models;
using BTCPayServer.Models.ManageViewModels;
using BTCPayServer.Services;
using BTCPayServer.Authentication;
using BTCPayServer.Wallet;
using Microsoft.AspNetCore.Hosting;
using NBitpayClient;
using NBitcoin;
using BTCPayServer.Stores;

namespace BTCPayServer.Controllers
{
	[Authorize]
	[Route("[controller]/[action]")]
	public class ManageController : Controller
	{
		private readonly UserManager<ApplicationUser> _userManager;
		private readonly SignInManager<ApplicationUser> _signInManager;
		private readonly IEmailSender _emailSender;
		private readonly ILogger _logger;
		private readonly UrlEncoder _urlEncoder;
		TokenRepository _TokenRepository;
		private readonly BTCPayWallet _Wallet;
		IHostingEnvironment _Env;
		IExternalUrlProvider _UrlProvider;
		StoreRepository _StoreRepository;


		private const string AuthenicatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";

		public ManageController(
		  UserManager<ApplicationUser> userManager,
		  SignInManager<ApplicationUser> signInManager,
		  IEmailSender emailSender,
		  ILogger<ManageController> logger,
		  UrlEncoder urlEncoder,
		  TokenRepository tokenRepository,
		  BTCPayWallet wallet,
		  StoreRepository storeRepository,
		  IHostingEnvironment env,
		  IExternalUrlProvider urlProvider)
		{
			_userManager = userManager;
			_signInManager = signInManager;
			_emailSender = emailSender;
			_logger = logger;
			_urlEncoder = urlEncoder;
			_TokenRepository = tokenRepository;
			_Wallet = wallet;
			_Env = env;
			_UrlProvider = urlProvider;
			_StoreRepository = storeRepository;
		}

		[TempData]
		public string StatusMessage
		{
			get; set;
		}

		[HttpGet]
		public async Task<IActionResult> Index()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}
			var store = await _StoreRepository.GetStore(user.Id);

			var model = new IndexViewModel
			{
				Username = user.UserName,
				Email = user.Email,
				PhoneNumber = user.PhoneNumber,
				IsEmailConfirmed = user.EmailConfirmed,
				StatusMessage = StatusMessage,
				ExtPubKey = store.DerivationStrategy,
				StoreWebsite = store.StoreWebsite,
				StoreName = store.StoreName,
				SpeedPolicy = store.SpeedPolicy
			};
			return View(model);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Index(IndexViewModel model)
		{
			if(!ModelState.IsValid)
			{
				return View(model);
			}

			bool needUpdate = false;

			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}
			var store = await _StoreRepository.GetStore(user.Id);

			if(model.ExtPubKey != store.DerivationStrategy)
			{
				store.DerivationStrategy = model.ExtPubKey;
				await _Wallet.TrackAsync(store.DerivationStrategy);
				needUpdate = true;
			}

			if(model.SpeedPolicy != store.SpeedPolicy)
			{
				store.SpeedPolicy = model.SpeedPolicy;
				needUpdate = true;
			}

			if(model.StoreName != store.StoreName)
			{
				store.StoreName = model.StoreName;
				needUpdate = true;
			}

			if(model.StoreWebsite != store.StoreWebsite)
			{
				store.StoreWebsite = model.StoreWebsite;
				needUpdate = true;
			}

			var email = user.Email;
			if(model.Email != email)
			{
				var setEmailResult = await _userManager.SetEmailAsync(user, model.Email);
				if(!setEmailResult.Succeeded)
				{
					throw new ApplicationException($"Unexpected error occurred setting email for user with ID '{user.Id}'.");
				}
			}

			var phoneNumber = user.PhoneNumber;
			if(model.PhoneNumber != phoneNumber)
			{
				var setPhoneResult = await _userManager.SetPhoneNumberAsync(user, model.PhoneNumber);
				if(!setPhoneResult.Succeeded)
				{
					throw new ApplicationException($"Unexpected error occurred setting phone number for user with ID '{user.Id}'.");
				}
			}

			if(needUpdate)
			{
				var result = await _userManager.UpdateAsync(user);
				await _StoreRepository.UpdateStore(store);
				if(!result.Succeeded)
				{
					throw new ApplicationException($"Unexpected error occurred updating user with ID '{user.Id}'.");
				}
			}

			StatusMessage = "Your profile has been updated";
			return RedirectToAction(nameof(Index));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SendVerificationEmail(IndexViewModel model)
		{
			if(!ModelState.IsValid)
			{
				return View(model);
			}

			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
			var callbackUrl = Url.EmailConfirmationLink(user.Id, code, Request.Scheme);
			var email = user.Email;
			await _emailSender.SendEmailConfirmationAsync(email, callbackUrl);

			StatusMessage = "Verification email sent. Please check your email.";
			return RedirectToAction(nameof(Index));
		}

		[HttpGet]
		public async Task<IActionResult> ChangePassword()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var hasPassword = await _userManager.HasPasswordAsync(user);
			if(!hasPassword)
			{
				return RedirectToAction(nameof(SetPassword));
			}

			var model = new ChangePasswordViewModel { StatusMessage = StatusMessage };
			return View(model);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
		{
			if(!ModelState.IsValid)
			{
				return View(model);
			}

			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var changePasswordResult = await _userManager.ChangePasswordAsync(user, model.OldPassword, model.NewPassword);
			if(!changePasswordResult.Succeeded)
			{
				AddErrors(changePasswordResult);
				return View(model);
			}

			await _signInManager.SignInAsync(user, isPersistent: false);
			_logger.LogInformation("User changed their password successfully.");
			StatusMessage = "Your password has been changed.";

			return RedirectToAction(nameof(ChangePassword));
		}

		[HttpGet]
		public async Task<IActionResult> SetPassword()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var hasPassword = await _userManager.HasPasswordAsync(user);

			if(hasPassword)
			{
				return RedirectToAction(nameof(ChangePassword));
			}

			var model = new SetPasswordViewModel { StatusMessage = StatusMessage };
			return View(model);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SetPassword(SetPasswordViewModel model)
		{
			if(!ModelState.IsValid)
			{
				return View(model);
			}

			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var addPasswordResult = await _userManager.AddPasswordAsync(user, model.NewPassword);
			if(!addPasswordResult.Succeeded)
			{
				AddErrors(addPasswordResult);
				return View(model);
			}

			await _signInManager.SignInAsync(user, isPersistent: false);
			StatusMessage = "Your password has been set.";

			return RedirectToAction(nameof(SetPassword));
		}

		[HttpGet]
		public async Task<IActionResult> ExternalLogins()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var model = new ExternalLoginsViewModel { CurrentLogins = await _userManager.GetLoginsAsync(user) };
			model.OtherLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync())
				.Where(auth => model.CurrentLogins.All(ul => auth.Name != ul.LoginProvider))
				.ToList();
			model.ShowRemoveButton = await _userManager.HasPasswordAsync(user) || model.CurrentLogins.Count > 1;
			model.StatusMessage = StatusMessage;

			return View(model);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> LinkLogin(string provider)
		{
			// Clear the existing external cookie to ensure a clean login process
			await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

			// Request a redirect to the external login provider to link a login for the current user
			var redirectUrl = Url.Action(nameof(LinkLoginCallback));
			var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl, _userManager.GetUserId(User));
			return new ChallengeResult(provider, properties);
		}

		[HttpGet]
		public async Task<IActionResult> LinkLoginCallback()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var info = await _signInManager.GetExternalLoginInfoAsync(user.Id);
			if(info == null)
			{
				throw new ApplicationException($"Unexpected error occurred loading external login info for user with ID '{user.Id}'.");
			}

			var result = await _userManager.AddLoginAsync(user, info);
			if(!result.Succeeded)
			{
				throw new ApplicationException($"Unexpected error occurred adding external login for user with ID '{user.Id}'.");
			}

			// Clear the existing external cookie to ensure a clean login process
			await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

			StatusMessage = "The external login was added.";
			return RedirectToAction(nameof(ExternalLogins));
		}

		[HttpGet]
		[Route("/api-access-request")]
		public async Task<IActionResult> AskPairing(string pairingCode)
		{
			var pairing = await _TokenRepository.GetPairingAsync(pairingCode);
			if(pairing == null)
			{
				StatusMessage = "Unknown pairing code";
				return RedirectToAction(nameof(Pairs));
			}
			else
			{
				return View(new PairingModel()
				{
					Id = pairing.Id,
					Facade = pairing.Facade,
					Label = pairing.Label,
					SIN = pairing.SIN
				});
			}
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Pairs(string pairingCode)
		{
			var store = await _StoreRepository.GetStore(_userManager.GetUserId(User));
			if(pairingCode != null && await _TokenRepository.PairWithAsync(pairingCode, store.Id))
			{
				StatusMessage = "Pairing is successfull";
				return RedirectToAction(nameof(Tokens));
			}
			else
			{
				StatusMessage = "Pairing failed";
				return RedirectToAction(nameof(Tokens));
			}
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> RemoveLogin(RemoveLoginViewModel model)
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var result = await _userManager.RemoveLoginAsync(user, model.LoginProvider, model.ProviderKey);
			if(!result.Succeeded)
			{
				throw new ApplicationException($"Unexpected error occurred removing external login for user with ID '{user.Id}'.");
			}

			await _signInManager.SignInAsync(user, isPersistent: false);
			StatusMessage = "The external login was removed.";
			return RedirectToAction(nameof(ExternalLogins));
		}

		[HttpGet]
		public async Task<IActionResult> TwoFactorAuthentication()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var model = new TwoFactorAuthenticationViewModel
			{
				HasAuthenticator = await _userManager.GetAuthenticatorKeyAsync(user) != null,
				Is2faEnabled = user.TwoFactorEnabled,
				RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user),
			};

			return View(model);
		}

		[HttpGet]
		public async Task<IActionResult> Disable2faWarning()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			if(!user.TwoFactorEnabled)
			{
				throw new ApplicationException($"Unexpected error occured disabling 2FA for user with ID '{user.Id}'.");
			}

			return View(nameof(Disable2fa));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Disable2fa()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var disable2faResult = await _userManager.SetTwoFactorEnabledAsync(user, false);
			if(!disable2faResult.Succeeded)
			{
				throw new ApplicationException($"Unexpected error occured disabling 2FA for user with ID '{user.Id}'.");
			}

			_logger.LogInformation("User with ID {UserId} has disabled 2fa.", user.Id);
			return RedirectToAction(nameof(TwoFactorAuthentication));
		}

		[HttpGet]
		public async Task<IActionResult> EnableAuthenticator()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			var unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
			if(string.IsNullOrEmpty(unformattedKey))
			{
				await _userManager.ResetAuthenticatorKeyAsync(user);
				unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
			}

			var model = new EnableAuthenticatorViewModel
			{
				SharedKey = FormatKey(unformattedKey),
				AuthenticatorUri = GenerateQrCodeUri(user.Email, unformattedKey)
			};

			return View(model);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> EnableAuthenticator(EnableAuthenticatorViewModel model)
		{
			if(!ModelState.IsValid)
			{
				return View(model);
			}

			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			// Strip spaces and hypens
			var verificationCode = model.Code.Replace(" ", string.Empty).Replace("-", string.Empty);

			var is2faTokenValid = await _userManager.VerifyTwoFactorTokenAsync(
				user, _userManager.Options.Tokens.AuthenticatorTokenProvider, verificationCode);

			if(!is2faTokenValid)
			{
				ModelState.AddModelError("model.Code", "Verification code is invalid.");
				return View(model);
			}

			await _userManager.SetTwoFactorEnabledAsync(user, true);
			_logger.LogInformation("User with ID {UserId} has enabled 2FA with an authenticator app.", user.Id);
			return RedirectToAction(nameof(GenerateRecoveryCodes));
		}


		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> AddToken(AddTokenViewModel model)
		{
			if(!ModelState.IsValid)
			{
				return View(model);
			}
			string storeId = await GetStoreId();

			var url = new Uri(_UrlProvider.GetAbsolute(""));
			var bitpay = new Bitpay(new NBitcoin.Key(), url);
			var pairing = await bitpay.RequestClientAuthorizationAsync(model.Label, new Facade(model.Facade));
			var link = pairing.CreateLink(url).ToString();
			await _TokenRepository.PairWithAsync(pairing.ToString(), storeId);
			StatusMessage = "New access token paired to this store";
			return RedirectToAction("Tokens");
		}

		private async Task<string> GetStoreId()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}
			return (await _StoreRepository.GetStore(user.Id)).Id;
		}

		[HttpGet]
		public IActionResult AddToken()
		{
			var model = new AddTokenViewModel();
			model.Facade = "merchant";
			if(_Env.IsDevelopment())
			{
				model.PublicKey = new Key().PubKey.ToHex();
			}
			return View(model);
		}

		[HttpPost]
		public async Task<IActionResult> DeleteToken(string name, string sin)
		{
			await _TokenRepository.DeleteToken(sin, name);
			StatusMessage = "Token revoked";
			return RedirectToAction("Tokens");
		}

		[HttpGet]
		public async Task<IActionResult> Tokens()
		{
			var model = new TokensViewModel();
			var tokens = await _TokenRepository.GetTokensByPairedIdAsync(await GetStoreId());
			model.StatusMessage = StatusMessage;
			model.Tokens = tokens.Select(t => new TokenViewModel()
			{
				Facade = t.Name,
				Label = t.Label,
				SIN = t.SIN,
				Id = t.Value
			}).ToArray();
			return View(model);
		}


		[HttpGet]
		public IActionResult ResetAuthenticatorWarning()
		{
			return View(nameof(ResetAuthenticator));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> ResetAuthenticator()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			await _userManager.SetTwoFactorEnabledAsync(user, false);
			await _userManager.ResetAuthenticatorKeyAsync(user);
			_logger.LogInformation("User with id '{UserId}' has reset their authentication app key.", user.Id);

			return RedirectToAction(nameof(EnableAuthenticator));
		}

		[HttpGet]
		public async Task<IActionResult> GenerateRecoveryCodes()
		{
			var user = await _userManager.GetUserAsync(User);
			if(user == null)
			{
				throw new ApplicationException($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
			}

			if(!user.TwoFactorEnabled)
			{
				throw new ApplicationException($"Cannot generate recovery codes for user with ID '{user.Id}' as they do not have 2FA enabled.");
			}

			var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
			var model = new GenerateRecoveryCodesViewModel { RecoveryCodes = recoveryCodes.ToArray() };

			_logger.LogInformation("User with ID {UserId} has generated new 2FA recovery codes.", user.Id);

			return View(model);
		}

		#region Helpers

		private void AddErrors(IdentityResult result)
		{
			foreach(var error in result.Errors)
			{
				ModelState.AddModelError(string.Empty, error.Description);
			}
		}

		private string FormatKey(string unformattedKey)
		{
			var result = new StringBuilder();
			int currentPosition = 0;
			while(currentPosition + 4 < unformattedKey.Length)
			{
				result.Append(unformattedKey.Substring(currentPosition, 4)).Append(" ");
				currentPosition += 4;
			}
			if(currentPosition < unformattedKey.Length)
			{
				result.Append(unformattedKey.Substring(currentPosition));
			}

			return result.ToString().ToLowerInvariant();
		}

		private string GenerateQrCodeUri(string email, string unformattedKey)
		{
			return string.Format(
				AuthenicatorUriFormat,
				_urlEncoder.Encode("BTCPayServer"),
				_urlEncoder.Encode(email),
				unformattedKey);
		}

		#endregion
	}
}