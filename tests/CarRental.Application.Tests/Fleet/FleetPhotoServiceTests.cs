using CarRental.Application.Fleet.Photos;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Fleet;

public sealed class FleetPhotoServiceTests
{
    private readonly FleetTestBed _bed = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    public static byte[] Png { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    public static byte[] WebP { get; } = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBPVP8 "u8];

    [Theory]
    [MemberData(nameof(Signatures))]
    public void The_real_type_is_read_from_the_file_signature(byte[] content, string? expected)
    {
        PhotoFile.Detect(content).ShouldBe(expected);
    }

    public static TheoryData<byte[], string?> Signatures() => new()
    {
        { Jpeg, StoredPhoto.Jpeg },
        { Png, StoredPhoto.Png },
        { WebP, StoredPhoto.WebP },
        { "GIF89a........"u8.ToArray(), null },
        { "%PDF-1.7 ......"u8.ToArray(), null },
        { "<svg xmlns=..."u8.ToArray(), null },
        { [0xFF, 0xD8], null },
        { "RIFF....WAVE"u8.ToArray(), null },
    };

    [Fact]
    public async Task A_vehicle_photo_is_stored_privately_under_the_vehicle()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());

        var result = await _bed.Photos().UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(Png), Ct);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        var photo = vehicle.Photos.ShouldHaveSingleItem();
        photo.Id.ShouldBe(result.Value);
        photo.ContentType.ShouldBe(StoredPhoto.Png);
        photo.Length.ShouldBe(Png.Length);
        photo.StoragePath.ShouldBe($"vehicles/{vehicle.Id:D}/{photo.Id:D}.png");
        _bed.Storage.Files.Keys.ShouldBe([(FleetMedia.Container, photo.StoragePath)]);
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unsupported_empty_or_too_large_files_are_refused_before_storage()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var photos = _bed.Photos();

        (await photos.UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream("GIF89a"u8.ToArray()), Ct)).Error.ShouldBe(FleetErrors.PhotoUnsupportedFormat);
        (await photos.UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(), Ct)).Error.ShouldBe(FleetErrors.PhotoEmpty);
        var tooLarge = new byte[StoredPhoto.MaxLength + 1];
        Jpeg.CopyTo(tooLarge, 0);
        (await photos.UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(tooLarge), Ct)).Error.ShouldBe(FleetErrors.PhotoTooLarge);
        (await photos.UploadVehiclePhotoAsync(Guid.CreateVersion7(), new MemoryStream(Jpeg), Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);

        _bed.Storage.Files.ShouldBeEmpty();
        vehicle.Photos.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_of_exactly_ten_megabytes_is_accepted()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var content = new byte[StoredPhoto.MaxLength];
        Jpeg.CopyTo(content, 0);

        (await _bed.Photos().UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(content), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Removing_a_vehicle_photo_deletes_the_stored_file()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var photoId = (await _bed.Photos().UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(Jpeg), Ct)).Value;

        (await _bed.Photos().RemoveVehiclePhotoAsync(vehicle.Id, photoId, Ct)).IsSuccess.ShouldBeTrue();
        (await _bed.Photos().RemoveVehiclePhotoAsync(vehicle.Id, photoId, Ct)).Error.ShouldBe(FleetErrors.PhotoNotFound);

        vehicle.Photos.ShouldBeEmpty();
        _bed.Storage.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Vehicle_photos_are_reserved_to_fleet_readers()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var photoId = (await _bed.Photos().UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(Jpeg), Ct)).Value;

        var asPreparer = await _bed.Photos(Roles.Preparer).FindVehiclePhotoAsync(vehicle.Id, photoId, Ct);
        var unknown = await _bed.Photos().FindVehiclePhotoAsync(vehicle.Id, Guid.CreateVersion7(), Ct);
        var anonymous = await new FleetPhotoService(_bed.Db, _bed.Writer(new CarRental.Application.Common.Security.ScopedUserContext([])), _bed.Storage, new CarRental.Application.Common.Security.ScopedUserContext([]), Microsoft.Extensions.Logging.Abstractions.NullLogger<FleetPhotoService>.Instance)
            .FindVehiclePhotoAsync(vehicle.Id, photoId, Ct);

        asPreparer.Value.ContentType.ShouldBe(StoredPhoto.Jpeg);
        unknown.Error.ShouldBe(FleetErrors.PhotoNotFound);
        anonymous.Error.ShouldBe(FleetErrors.Forbidden);
        (await _bed.Photos(Roles.Agent).UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(Jpeg), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        await using var download = await _bed.Photos().OpenAsync(asPreparer.Value, Ct);
        download.ShouldNotBeNull().Length.ShouldBe(Jpeg.Length);
    }

    [Fact]
    public async Task A_category_photo_replaces_the_previous_one_and_is_public()
    {
        var category = _bed.AddCategory();

        (await _bed.Photos().UploadCategoryPhotoAsync(category.Id, new MemoryStream(Jpeg), Ct)).IsSuccess.ShouldBeTrue();
        var first = category.PhotoPath;
        (await _bed.Photos().UploadCategoryPhotoAsync(category.Id, new MemoryStream(WebP), Ct)).IsSuccess.ShouldBeTrue();

        category.PhotoContentType.ShouldBe(StoredPhoto.WebP);
        category.PhotoPath.ShouldNotBe(first);
        _bed.Storage.Files.Keys.ShouldBe([(FleetMedia.Container, category.PhotoPath!)]);
        var found = await _bed.Photos(Roles.Agent).FindCategoryPhotoAsync(category.Id, Ct);
        found.ShouldBe(new PhotoDescriptor(category.PhotoPath!, StoredPhoto.WebP, category.PhotoHash!));
        (await _bed.Queries().GetCategoryAsync(category.Id, Ct))!.PhotoUrl.ShouldBe($"/media/categories/{category.Id:D}?v={category.PhotoVersion}");

        (await _bed.Photos().RemoveCategoryPhotoAsync(category.Id, Ct)).IsSuccess.ShouldBeTrue();
        _bed.Storage.Files.ShouldBeEmpty();
        (await _bed.Photos().FindCategoryPhotoAsync(category.Id, Ct)).ShouldBeNull();
        (await _bed.Queries().GetCategoryAsync(category.Id, Ct))!.PhotoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task A_vehicle_has_twelve_photos_at_most()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        for (var i = 0; i < Vehicle.MaxPhotos; i++)
        {
            (await _bed.Photos().UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(Jpeg), Ct)).IsSuccess.ShouldBeTrue();
        }

        (await _bed.Photos().UploadVehiclePhotoAsync(vehicle.Id, new MemoryStream(Jpeg), Ct)).Error.ShouldBe(FleetErrors.TooManyPhotos);
        _bed.Storage.Files.Count.ShouldBe(Vehicle.MaxPhotos);
    }
}
