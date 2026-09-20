using Paysys.DAL.Entities;

namespace Paysys.BLL.Services;

public record CardResolution(CardToken CardToken, Account Account, Bank Bank);
