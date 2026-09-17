using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SqlRestoreManager.Data;

namespace SqlRestoreManager.Controllers;

public sealed class HistoryController : Controller
{
    private const int MaxRows = 200;

    private readonly AppDbContext _db;

    public HistoryController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var items = await _db.RestoreHistory
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .Take(MaxRows)
            .ToListAsync(ct);

        return View(items);
    }
}
